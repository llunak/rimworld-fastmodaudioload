using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.IO;
using System.Threading;
using Verse;
using RimWorld.IO;
using UnityEngine;
using UnityEngine.Networking;
using KTrie;

// Switch RimWorld to load large audio files from mods using streaming, instead of loading
// the entire file at startup.

// The code to patch is in ModContentLoader<T>.LoadItem(), but since Harmony patching of generics
// is problematic, patch the lowest non-generic function in the call chain and then copy&paste
// all the relevant code below :(.
namespace FastModAudioLoad
{
    // Using [StaticConstructorOnStartup] is too late, run Harmony patching in a Mod subclass.
    public class FastModAudioLoadMod : Mod
    {
        public FastModAudioLoadMod(ModContentPack mod)
            : base(mod)
        {
            var harmony = new Harmony("llunak.FastModAudioLoad");
            harmony.PatchAll(Assembly.GetExecutingAssembly());
        }
    }

    [HarmonyPatch(typeof(ModContentPack))]
    public static class ModContentPack_Patch
    {
        [HarmonyTranspiler]
        [HarmonyPatch(nameof(ReloadContentInt))]
        public static IEnumerable<CodeInstruction> ReloadContentInt(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            bool found = false;
            for( int i = 0; i < codes.Count; ++i )
            {
                // Log.Message("T:" + i + ":" + codes[i].opcode + "::" + (codes[i].operand != null ? codes[i].operand.ToString() : codes[i].operand));
                if( codes[ i ].opcode == OpCodes.Ldfld
                    && codes[ i ].operand.ToString().EndsWith(" audioClips" )
                    && i + 2 < codes.Count
                    && codes[ i + 1 ].IsLdarg()
                    && codes[ i + 2 ].opcode == OpCodes.Callvirt && codes[ i + 2 ].operand.ToString() == "Void ReloadAll(Boolean)" )
                {
                    codes.Insert( i + 2, new CodeInstruction( OpCodes.Ldarg_0 )); // load 'this'
                    codes[ i + 3 ] = new CodeInstruction( OpCodes.Call,
                        typeof( ModContentPack_Patch ).GetMethod( nameof( ReloadContentInt_Hook )));
                    found = true;
                    break;
                }
            }
            if(!found)
                Log.Error("FastModAudioLoad: Failed to patch ModContentPack.ReloadContentInt()");
            return codes;
        }

        public static void ReloadContentInt_Hook(ModContentHolder<AudioClip> audioClips, bool hotReload, ModContentPack mod)
        {
            ReloadAll(audioClips, hotReload, mod);
        }

        // This is ModContentHolder<T>.ReloadAll(), with modifications (mostly T->AudioClip).
        public static void ReloadAll(ModContentHolder<AudioClip> audioClips, bool hotReload, ModContentPack mod)
        {
		foreach (Pair<string, LoadedContentItem<AudioClip>> item in LoadAllForMod(mod))
		{
			string first = item.First;
			first = first.Replace('\\', '/');
			string text = GenFilePaths.ContentPath<AudioClip>();
			if (first.StartsWith(text))
			{
				first = first.Substring(text.Length);
			}
			if (first.EndsWith(Path.GetExtension(first)))
			{
				first = first.Substring(0, first.Length - Path.GetExtension(first).Length);
			}
			if (audioClips.contentList.ContainsKey(first))
			{
				if (!hotReload)
				{
					Log.Warning(string.Concat("Tried to load duplicate ", typeof(AudioClip), " with path: ", item.Second.internalFile, " and internal path: ", first));
				}
			}
			else
			{
				audioClips.contentList.Add(first, item.Second.contentItem);
				audioClips.contentListTrie.Add(first);
				if (item.Second.extraDisposable != null)
				{
					audioClips.extraDisposables.Add(item.Second.extraDisposable);
				}
			}
		}
	}

        // This is ModContentLoader<T>.LoadAllForMod(), with modifications (mostly T->AudioClip).
	public static IEnumerable<Pair<string, LoadedContentItem<AudioClip>>> LoadAllForMod(ModContentPack mod)
	{
		DeepProfiler.Start(string.Concat("Loading assets of type ", typeof(AudioClip), " for mod ", mod));
		Dictionary<string, FileInfo> allFilesForMod = ModContentPack.GetAllFilesForMod(mod, GenFilePaths.ContentPath<AudioClip>(),
		    ModContentLoader<AudioClip>.IsAcceptableExtension);
		List<(string, FileInfo, UnityWebRequest)> requests = [];
		foreach (KeyValuePair<string, FileInfo> item in allFilesForMod)
		{
			UnityWebRequest request = startLoadItem((FilesystemFile)item.Value);
			if(request != null)
			    requests.Add((item.Key, item.Value, request));
		}
		foreach ((string, FileInfo, UnityWebRequest) item in requests)
		{
			while(!item.Item3.isDone)
				Thread.Sleep(1);
			LoadedContentItem<AudioClip> loadedContentItem = finishLoadItem((FilesystemFile)item.Item2,item.Item3);
			if (loadedContentItem != null)
				yield return new Pair<string, LoadedContentItem<AudioClip>>(item.Item1, loadedContentItem);
		}
		DeepProfiler.End();
	}

        public static UnityWebRequest startLoadItem(VirtualFile file)
        {
		string uri = GenFilePaths.SafeURIForUnityWWWFromPath(file.FullPath);
		UnityWebRequest unityWebRequest = UnityWebRequestMultimedia.GetAudioClip(uri, GetAudioTypeFromURI(uri));
		((DownloadHandlerAudioClip)unityWebRequest.downloadHandler).streamAudio = ShouldStreamAudioClipFromFile(file);
		unityWebRequest.SendWebRequest();
		return unityWebRequest;
	}

        public static LoadedContentItem<AudioClip> finishLoadItem(VirtualFile file, UnityWebRequest unityWebRequest)
        {
		try
		{
			if (unityWebRequest.error != null)
			{
				Log.Error(string.Concat("Exception loading ", typeof(AudioClip), " from file.\nabsFilePath: ", file.FullPath, "\nException: ", unityWebRequest.error));
				return null;
			}
			AudioClip val = DownloadHandlerAudioClip.GetContent(unityWebRequest);
			UnityEngine.Object @object = val as UnityEngine.Object;
			if (@object != null)
			{
				@object.name = Path.GetFileNameWithoutExtension(file.Name);
			}
			IDisposable extraDisposable = null;
			return new LoadedContentItem<AudioClip>(file, val, extraDisposable);
		}
		catch (Exception ex)
		{
			Log.Error(string.Concat("Exception loading ", typeof(AudioClip), " from file.\nabsFilePath: ", file.FullPath, "\nException: ", ex.ToString()));
		}
		return null;
        }

        private static AudioType GetAudioTypeFromURI(string uri)
        {
            return ModContentLoader<AudioClip>.GetAudioTypeFromURI(uri);
        }

        private static bool ShouldStreamAudioClipFromFile(VirtualFile file)
        {
            return ModContentLoader<AudioClip>.ShouldStreamAudioClipFromFile(file);
        }
    }
}
