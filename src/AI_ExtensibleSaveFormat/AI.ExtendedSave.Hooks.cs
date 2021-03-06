﻿using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Emit;
using AIProject.SaveData;
using AIProject.UI;
using BehaviorDesigner.Runtime.Tasks.Unity.UnityPlayerPrefs;
using CharaCustom;
using HarmonyLib;
using Housing;
using MessagePack;

namespace ExtensibleSaveFormat
{
    public partial class ExtendedSave
    {
        internal static partial class Hooks
        {
            const string MainGameSaveMarker = "WD";

            //Override ExtSave for list loading at game startup
            [HarmonyPrefix]
            [HarmonyPatch(typeof(CustomCharaFileInfoAssist), nameof(CustomCharaFileInfoAssist.CreateCharaFileInfoList))]
            [HarmonyPatch(typeof(CustomClothesFileInfoAssist), nameof(CustomClothesFileInfoAssist.CreateClothesFileInfoList))]
            [HarmonyPatch(typeof(GameCoordinateFileInfoAssist), nameof(GameCoordinateFileInfoAssist.CreateCoordinateFileInfoList))]
            private static void CreateListPrefix() => LoadEventsEnabled = false;

            [HarmonyPostfix]
            [HarmonyPatch(typeof(CustomCharaFileInfoAssist), nameof(CustomCharaFileInfoAssist.CreateCharaFileInfoList))]
            [HarmonyPatch(typeof(CustomClothesFileInfoAssist), nameof(CustomClothesFileInfoAssist.CreateClothesFileInfoList))]
            [HarmonyPatch(typeof(GameCoordinateFileInfoAssist), nameof(GameCoordinateFileInfoAssist.CreateCoordinateFileInfoList))]
            private static void CreateListPostfix() => LoadEventsEnabled = true;


            public static void InstallMainGameHooks(Harmony harmony)
            {
                // Minimizing the interference between games to limiting the harmony hook initialization
                // MainGame Save/Load - PostFix
                // TODO: Make sure everything works with Steam AND JP Version.
                harmony.Patch(
                    typeof(SaveData).GetMethod("Load", new[] {typeof(BinaryReader)}),
                    null, new HarmonyMethod(typeof(Hooks).GetMethod(nameof(OnLoadMainGame)))
                );
                harmony.Patch(
                    typeof(SaveData).GetMethod("SaveFile", new[] {typeof(BinaryWriter)}),
                    null, new HarmonyMethod(typeof(Hooks).GetMethod(nameof(OnSaveMainGame)))
                );
                harmony.Patch(
                    typeof(CraftInfo).GetMethod("Load", new[] {typeof(string)}),
                    null, null, new HarmonyMethod(typeof(Hooks).GetMethod(nameof(OnCraftLoad)))
                );
                harmony.Patch(
                    typeof(CraftInfo).GetMethod("Save", new[] {typeof(string), typeof(byte[])}),
                    null, null, new HarmonyMethod(typeof(Hooks).GetMethod(nameof(OnCraftSave)))
                );
            }

            public static void GameDataLoadHook(AIProject.SaveData.SaveData saveData, BinaryReader br)
            {
                try
                {
                    string marker = br.ReadString();
                    int version = br.ReadInt32();
                    int length = br.ReadInt32();
                    if (marker == Marker && version == DataVersion && length > 0)
                        ReadWorldData(saveData, br);
                }
                catch (EndOfStreamException)
                {
                } //Incomplete/non-existant data
                catch (System.SystemException)
                {
                } //Invalid/unexpected deserialized data
            }

            public static void ReadWorldData(SaveData saveData, BinaryReader br)
            {
                var marker = br.ReadString();
                if (marker != MainGameSaveMarker) return;

                var index = br.ReadInt32();
                var length = br.ReadInt32();
                if (length <= 0) return;

                var bytes = br.ReadBytes(length);
                var dictionary = MessagePackSerializer.Deserialize<Dictionary<string, PluginData>>(bytes);

                if (index < 0)
                {
                    var worldData = saveData.AutoData;
                    if (worldData == null) return;
                    internalWorldDataDictionary.Set(worldData, dictionary);
                }
                else
                {
                    if (saveData.WorldList.TryGetValue(index, out var worldData))
                        internalWorldDataDictionary.Set(worldData, dictionary);
                }

                // insert world datat load event
                ReadWorldData(saveData, br); // Recursively search next entry.
                Logger.LogMessage($"Loaded world data for {index}");
            }

            public static void WriteWorldData(int key, WorldData worldData, BinaryWriter bw)
            {
                if (worldData == null) return;

                Dictionary<string, PluginData> extendedData = GetAllExtendedData(worldData);
                if (extendedData == null)
                    return;

                // insert world datat save event

                byte[] data = MessagePackSerializer.Serialize(extendedData);
                bw.Write("WD"); // Two bytes of marker that indicates there is extended world data ahead
                bw.Write(key);
                bw.Write(data.Length);
                bw.Write(data);
            }

            public static void GameDataSaveHook(AIProject.SaveData.SaveData saveData, BinaryWriter bw)
            {
                Logger.Log(BepInEx.Logging.LogLevel.Debug, "Save Game Data Hook!");

                MainGameSaveWriteEvent(saveData);
                bw.Write(Marker);
                bw.Write(DataVersion);

                // AI's GameSave contains multiple world data.
                // unfortunately it will try to save all the world data so we need to fix it.
                WorldData autoSaveData = saveData.AutoData;
                WriteWorldData(-1, saveData.AutoData, bw);
                foreach (var kv in saveData.WorldList)
                {
                    WriteWorldData(kv.Key, kv.Value, bw);
                    Logger.LogMessage($"Saved world data for {kv.Key}");
                }
            }

            public static void HousingDataLoadHook(CraftInfo craftInfo, BinaryReader br)
            {
                try
                {
                    string marker = br.ReadString();
                    int version = br.ReadInt32();
                    int length = br.ReadInt32();
                    if (marker == Marker && version == DataVersion && length > 0)
                    {
                        byte[] bytes = br.ReadBytes(length);
                        var dictionary = MessagePackSerializer.Deserialize<Dictionary<string, PluginData>>(bytes);

                        internalCraftInfoDictionary.Set(craftInfo, dictionary);
                        HousingReadEvent(craftInfo);
                        Logger.LogMessage("Successfully loaded housing extended informations");
                    }
                    else Logger.LogMessage("empty game data extended");
                }
                catch (EndOfStreamException)
                {
                } //Incomplete/non-existant data
                catch (System.SystemException)
                {
                } //Invalid/unexpected deserialized data
            }

            public static void HousingDataSaveHook(CraftInfo craftInfo, BinaryWriter bw)
            {
                HousingWriteEvent(craftInfo);
                Dictionary<string, PluginData> extendedData = GetAllExtendedData(craftInfo);
                if (extendedData == null)
                    return;

                byte[] data = MessagePackSerializer.Serialize(extendedData);
                bw.Write(Marker);
                bw.Write(DataVersion);
                bw.Write(data.Length);
                bw.Write(data);
            }

            public static void OnLoadMainGame(SaveData __instance, BinaryReader reader, bool __result)
            {
                if (!__result) return;
                Logger.LogMessage("Loaded the main game data");
                GameDataLoadHook(__instance, reader);
            }

            public static void OnSaveMainGame(SaveData __instance, BinaryWriter writer) => GameDataSaveHook(__instance, writer);

            public static IEnumerable<CodeInstruction> OnCraftSave(IEnumerable<CodeInstruction> instructions)
            {
                var set = false;
                var instructionsList = instructions.ToList();
                for (var i = 0; i < instructionsList.Count; i++)
                {
                    var inst = instructionsList[i];
                    yield return inst;
                    if (set || inst.opcode != OpCodes.Callvirt || instructionsList[i + 1].opcode != OpCodes.Leave) continue;

                    // [0] FileStream, [1] BinaryWriter, [2] CraftInfoBytes
                    yield return new CodeInstruction(OpCodes.Ldarg_0); // Load Parameter 0: self
                    yield return new CodeInstruction(OpCodes.Ldloc_1); // Load local value 1: binary writer
                    yield return new CodeInstruction(OpCodes.Call, typeof(Hooks).GetMethod(nameof(OnWriteHousingCard)));
                    set = true;
                }
            }

            public static void OnWriteHousingCard(CraftInfo instance, BinaryWriter writer)
            {
                Logger.LogMessage("Saved the housing card");
                HousingDataSaveHook(instance, writer);
            }

            public static IEnumerable<CodeInstruction> OnCraftLoad(IEnumerable<CodeInstruction> instructions)
            {
                // to counter false opcode injection...
                var instAnchor = typeof(Housing.CraftInfo).GetMethod("Load", new[] {typeof(byte[])});
                var set = false;
                var instructionsList = instructions.ToList();
                for (var i = 0; i < instructionsList.Count; i++)
                {
                    var inst = instructionsList[i];
                    yield return inst;
                    if (i == 0) continue;
                    var prevInstruction = instructionsList[i - 1];
                    if (set || prevInstruction == null || !prevInstruction.Is(OpCodes.Call, instAnchor)) continue;

                    // [0] Filestream [1] boolean [2] BinaryReader [3]  int64, [4] version [5] unit8[] [6] exception
                    // invoke read housing card
                    yield return new CodeInstruction(OpCodes.Ldarg_1); // Load parameter 0 : self
                    yield return new CodeInstruction(OpCodes.Ldloc_2); // Load local value 1: binary reader
                    yield return new CodeInstruction(OpCodes.Call, typeof(Hooks).GetMethod(nameof(OnReadHousingCard)));
                    set = true;
                }
            }

            public static void OnReadHousingCard(CraftInfo instance, BinaryReader reader)
            {
                Logger.LogMessage("Loaded the housing card");
                HousingDataLoadHook(instance, reader);
            }
        }
    }
}
