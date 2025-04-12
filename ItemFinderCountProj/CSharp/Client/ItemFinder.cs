
using System;
using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Barotrauma;
using HarmonyLib;
using OpCodes = System.Reflection.Emit.OpCodes;
using MiniMap = Barotrauma.Items.Components.MiniMap;
// ReSharper disable UnusedType.Global

[assembly: IgnoresAccessChecksTo("Barotrauma")]
namespace ItemFinderCount {
    
    class ItemFinderCountMain : IAssemblyPlugin
    {
        // Internal
        private Harmony HarmonyInstance;
        private static readonly List<Action> RevertActions = new();
        private const string PatchCategoryString = "amadare.ItemFinderCount";
        
        // Domain
        private static Dictionary<Identifier, SearchResults> SearchCache = new();

        public void Stop()
        {
            this.HarmonyInstance.UnpatchSelf();

            foreach (var reventAction in RevertActions)
            {
                try
                {
                    reventAction();
                }
                catch (Exception ex)
                {
                    Log("Error occured while executing ReventAction: " + ex);
                }
            }
            RevertActions.Clear();
            SearchCache.Clear();

        }
        public void Initialize()
        {
            this.HarmonyInstance = new Harmony(PatchCategoryString);
            this.HarmonyInstance.Patch(
                original: AccessTools.Method(typeof(MiniMap), nameof(MiniMap.UpdateSearchTooltip)), 
                postfix:new HarmonyMethod(AccessTools.Method(typeof(MiniMap_UpdateSearchTooltip_Patch),nameof(MiniMap_UpdateSearchTooltip_Patch.Postfix))));
            this.HarmonyInstance.Patch(
                original: AccessTools.Method(typeof(MiniMap), nameof(MiniMap.CreateItemFrame)),
                transpiler: new HarmonyMethod(AccessTools.Method(typeof(MiniMap_CreateItemFrame_Patch), nameof(MiniMap_CreateItemFrame_Patch.Transpiler))));
        }

        public void OnLoadCompleted()
        {
        }

        public void PreInitPatching()
        {
        }

        public void Dispose()
        {
            Stop();
        }

        public static class MiniMap_UpdateSearchTooltip_Patch
        {
            internal static void Postfix(MiniMap __instance)
            {
                UpdateSearchCache(__instance);
                UpdateSearchItemFrames(__instance);
            }
        }
        
        public static class MiniMap_CreateItemFrame_Patch
        {
            internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var ops = new List<CodeInstruction>(instructions);

                //  nameText.>RectTransform<.SizeChanged += () => ...
                var idx = ops.FindLastIndex(op =>
                    op.opcode == OpCodes.Callvirt &&
                    op.operand as MethodInfo == AccessTools.PropertyGetter(typeof(GUIComponent), "RectTransform"));
                
                if (idx == -1)
                {
                    Log("Failed to patch CreateItemFrame");
                    return ops;
                }
                
                // add call to PatchNameText
                ops.InsertRange(ops.Count - 1, new [] {
                    new CodeInstruction(OpCodes.Ldarg_0),
                    //new CodeInstruction(OpCodes.Ldarg_1),
                    ops[idx - 2], // ldloc.0 <closure class>
                    ops[idx - 1],  // ldfld <nameText>
                    new CodeInstruction(OpCodes.Call, typeof(MiniMap_CreateItemFrame_Patch).GetMethod(nameof(PatchNameText), BindingFlags.Static | BindingFlags.Public)!)
                });
                
                // removing SizeChanged subscription
                ops.RemoveRange(idx - 2, 7);
                
                Log("MiniMap.CreateItemFrame patched");
                return ops;
            }

            public static void PatchNameText(ItemPrefab itemPrefab, GUITextBlock nameText)
            {
                nameText.UserData = itemPrefab;
                var rectTransformOnSizeChanged = () =>
                {
                    UpdateNameText(nameText);
                    nameText.Text = ToolBox.LimitString(nameText.Text, nameText.Font, nameText.Rect.Width);
                };
                nameText.RectTransform.SizeChanged += rectTransformOnSizeChanged;
                RevertActions.Add(() => nameText.RectTransform.SizeChanged -= rectTransformOnSizeChanged);
            }
        }

        private static void UpdateSearchCache(MiniMap miniMap)
        {
            var listBox = miniMap.GetPrivateField<MiniMap, GUIComponent>("searchAutoComplete").GetChild<GUIListBox>();
            if (listBox?.Content == null) 
                return;

            var prefabsToSearch = listBox.Content.Children
                .Where(itemFrame => itemFrame.Visible)
                .Select(itemFrame => itemFrame.UserData)
                .Cast<ItemPrefab>()
                .ToList();
            foreach (var result in SearchForPrefab(miniMap, prefabsToSearch))
            {
                SearchCache[result.PrefabId] = result;
            }
        }
        
        public static void UpdateSearchItemFrames(MiniMap miniMap)
        {
            var listBox = miniMap.GetPrivateField<MiniMap, GUIComponent>("searchAutoComplete").GetChild<GUIListBox>();
            if (listBox?.Content == null) return;
            
            foreach (var itemFrame in listBox.Content.Children)
            {
                if (!itemFrame.Visible) continue;
                if (itemFrame.FindChild(child => child.GetType() == typeof(GUITextBlock), true) is not GUITextBlock nameText)
                {
                    Log("Cannot find GUITextBlock in itemFrame" + itemFrame.UserData);
                    continue;
                }

                UpdateNameText(nameText);
            }
        }

        static void UpdateNameText(GUITextBlock nameText)
        {
            var prefab = (ItemPrefab)nameText?.UserData;
            if (prefab == null)
            {
                return;
            }
            
            if (SearchCache.TryGetValue(prefab.Identifier, out var results) && results.OnSub > 0)
            {
                var builder = new StringBuilder(" [");
                if (results.OnSub != results.NonEmpty)
                {
                    builder.Append(results.NonEmpty).Append('/');
                }

                builder.Append(results.OnSub).Append(']');
                if (results.Carried != 0)
                {
                    builder.Append(" (+")
                        .Append(results.Carried)
                        .Append(" carried)");
                }

                var additionalString = builder.ToString();
                
                var additionalWidth = nameText.Font.MeasureString(additionalString);
                nameText.Text = ToolBox.LimitString(prefab.Name, nameText.Font, nameText.Rect.Width - (int)additionalWidth.X) + additionalString;
            }
            else if (results is { Carried: > 0 })
            {
                nameText.Text = ToolBox.LimitString($"{prefab.Name} ({results.Carried} carried)", nameText.Font, nameText.Rect.Width);
            } 
            else 
            {
                nameText.Text = ToolBox.LimitString(prefab.Name, nameText.Font, nameText.Rect.Width);
            }
        }
        
        static IReadOnlyCollection<SearchResults> SearchForPrefab(MiniMap miniMap, List<ItemPrefab> searchedPrefabs)
        {
            if (miniMap == null) 
                return ArraySegment<SearchResults>.Empty;

            var dict = searchedPrefabs.ToDictionary(p => p.Identifier, p => new SearchResults(p.Identifier));
            
            foreach (var it in Item.ItemList)
            {
                if (!dict.TryGetValue(it.Prefab.Identifier, out var searchedPrefab))
                    continue;
                
                if (!ReflectionHelper.VisibleOnItemFinder(miniMap, it))
                    continue;
                
                // ignore hidden items
                if (it.FindParentInventory(inv => inv is ItemInventory { Owner: Item { HiddenInGame: true }}) is { }) { continue; }
                
                // count carried
                var characterInventory = it.FindParentInventory(inv => inv is CharacterInventory) as CharacterInventory;
                if (characterInventory != null)
                {
                    if (characterInventory.Owner is Character character && character.IsOnPlayerTeam)
                    {
                        searchedPrefab.Carried++;
                    }
                }
                else
                {
                    searchedPrefab.OnSub++;
                    if (it.ConditionPercentage > .5f)
                    {
                        searchedPrefab.NonEmpty++;
                    }
                }
            }

            return dict.Values;
        }

        class SearchResults
        {
            public int OnSub;
            public int Carried;
            public int NonEmpty;
            public Identifier PrefabId;

            public SearchResults(int onSub, int carried, int nonEmpty, string prefabId)
            {
                this.OnSub = onSub;
                this.Carried = carried;
                this.NonEmpty = nonEmpty;
                this.PrefabId = prefabId;
            }

            public SearchResults(Identifier prefabId)
            {
                this.PrefabId = prefabId;
            }

            public override string ToString()
            {
                return $"({this.Carried}/{this.OnSub}/{this.NonEmpty})";
            }
        }

        public static void Log(string arg)
        {
            var line = "[ItemFinderCount] " + arg;
            LuaCsLogger.Log(line);
        }
    }
}
