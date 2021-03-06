using System;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using ScrollsModLoader.Interfaces;
using JsonFx.Json;
using Mono.Cecil;
using UnityEngine;

namespace ScrollsModLoader
{

	public class ModManager
	{

		private String modsPath;
		private ModLoader loader;
		public RepoManager repoManager;

		public List<Item> installedMods = new List<Item> ();

		public ModManager(ModLoader loader) {
		
			Platform.ErrorLog("ModManager creator");
			modsPath = Platform.getModsPath();

			if (!Directory.Exists (modsPath))
				Directory.CreateDirectory (modsPath);

			this.loader = loader;
			repoManager = new RepoManager (this);

			String installPath = Platform.getGlobalScrollsInstallPath();
			String modLoaderPath = Platform.getModLoaderPath() + System.IO.Path.DirectorySeparatorChar;




			Platform.ErrorLog("loadInstalledMods");
			this.loadInstalledMods ();
			Platform.ErrorLog("checkForUpdates");
			this.checkForUpdates   ();
			Platform.ErrorLog("sortInstalledMods");
			this.sortInstalledMods ();
		}

		public void sortInstalledMods() {
			installedMods.Sort (delegate(Item mod1, Item mod2) { //-1 higher, 0 equal, 1 lower

				if ((loader.modOrder.Contains ((mod1 as LocalMod).localId) && (mod1 as LocalMod).enabled) && (loader.modOrder.Contains ((mod2 as LocalMod).localId) && (mod2 as LocalMod).enabled)) {
					return loader.modOrder.IndexOf ((mod1 as LocalMod).localId) - loader.modOrder.IndexOf ((mod2 as LocalMod).localId);
				} else if (loader.modOrder.Contains ((mod1 as LocalMod).localId) && (mod1 as LocalMod).enabled) {
					return -1;
				} else if (loader.modOrder.Contains ((mod2 as LocalMod).localId) && (mod2 as LocalMod).enabled) {
					return 1;
				} else {
					return 0;
				}
			});
		}

		public void loadInstalledMods() {

			List<String> folderList = (from subdirectory in Directory.GetDirectories(modsPath)
			                       where Directory.GetFiles(subdirectory, "*.mod.dll").Length != 0
			                       select subdirectory).ToList();

			foreach (String folder in folderList) {
				LocalMod mod = null;
				if (File.Exists (folder + Path.DirectorySeparatorChar + "config.json")) {
					JsonReader reader = new JsonReader ();
					mod = (LocalMod) reader.Read (File.ReadAllText (folder + Path.DirectorySeparatorChar + "config.json"), typeof(LocalMod));
					if (mod.queueForUninstall) {
						removeMod (mod);
						continue;
					}
				} else {
					//new local installed mod
					Mod localMod = loader.loadModStatic (Directory.GetFiles (folder, "*.mod.dll") [0]);
					if (localMod != null) {
						mod = new LocalMod(true, (Directory.GetFiles (folder, "*.mod.dll") [0]), this.generateUniqueID(), null, null, true, localMod.name, localMod.description, localMod.version, localMod.versionCode);
						updateConfig(mod);
						loader.queueRepatch ();
					}
				}
				if (mod != null)
					installedMods.Add (mod);
			}

		}

		public void checkForUpdates() {
			foreach (Item mod in installedMods.ToList()) {
				if (!(mod as LocalMod).localInstall) {
					Mod onlineMod = repoManager.getModOnRepo ((mod as LocalMod).source, (mod as LocalMod));
					if (onlineMod == null)
						continue;
					if (onlineMod.version > (mod as LocalMod).version) {
						(mod as LocalMod).version = onlineMod.version;
						this.updateMod ((mod as LocalMod));
					}
				}
			}
		}



		public void installMod(Repo repo, Mod mod) {

			String newID = this.generateUniqueID ();
			String installPath = modsPath + Path.DirectorySeparatorChar + newID + Path.DirectorySeparatorChar + mod.name + ".mod.dll";

			LocalMod lmod = new LocalMod (false, installPath, newID, mod.id, repo, false, mod.name, mod.description, mod.version, mod.versionCode); 

			String folder = modsPath + Path.DirectorySeparatorChar + newID + Path.DirectorySeparatorChar;
			if (Directory.Exists (folder))
			{
				Directory.Delete (folder);
			}
			Directory.CreateDirectory (folder);

			if (this.updateMod (lmod)) {
				this.installedMods.Add (lmod);

				//add hooks
				loader.loadModStatic (lmod.installPath);
			} else {
				Extensions.DeleteDirectory (folder);
			}
		}

		public void deinstallMod(LocalMod mod) {
			loader.unloadMod (mod);
			mod.queueForUninstall = true;
			updateConfig (mod);
		}

		public void removeMod(LocalMod mod) {
			installedMods.Remove (mod);
			String folder = Path.GetDirectoryName(mod.installPath);
			if (Directory.Exists (folder))
			{
				Extensions.DeleteDirectory(folder);
			}
		}

		public bool updateMod(LocalMod mod) {
			if (this.updateFile(mod))
			{
				this.updateConfig (mod);
				loader.queueRepatch ();
				return true;
			}
			return false;
		}

		public bool downloadMod(LocalMod mod, String location) {
			Console.WriteLine (mod.source.url + "download/mod/" + mod.id);
			WebClientTimeOut wc = new WebClientTimeOut();

			try {
				wc.DownloadFile (new Uri(mod.source.url + "download/mod/" + mod.id), location);
			} catch (WebException) {
				return false;
			}

			// now check whether the downloaded file is actually a mod, and not an error
			String[] keys = wc.ResponseHeaders.AllKeys;

			String contentType = "";
			for (int i = 0; i < keys.Length; i++)
			{
				if (keys[i].Equals("Content-Type"))
				{
					contentType = wc.ResponseHeaders.Get(i);
				}
			}

			if (contentType.Equals("application/x-msdos-program")) // success
			{
				return true;
			}
			else 
			{   // from github its an text/plain file
				if (mod.source.url.StartsWith ("https://raw.github.com/") && (contentType.Equals("text/plain") || contentType.Equals("application/octet-stream")))
				{
					return true;
				}
				else // for example "application/json" or none at all, error
				{
					return false;
				}
			}
		}
		
		public bool updateFile(LocalMod mod) {
			String folder = modsPath + Path.DirectorySeparatorChar + mod.localId + Path.DirectorySeparatorChar;
			String filePath = folder + mod.name + ".mod.dll";
			
			return this.downloadMod(mod, filePath);
			
			/*
			byte[] modData = this.downloadMod(mod);

			File.Delete (filePath);
			FileStream modFile = File.Create (filePath);
			modFile.Write (modData, 0, modData.Length);
			modFile.Flush ();
			modFile.Close ();
			*/
		}

		public void updateConfig(LocalMod mod) {
			//update config
			String folder = Path.GetDirectoryName (mod.installPath) + Path.DirectorySeparatorChar;
			if (File.Exists(folder + "config.json"))
				File.Delete (folder + "config.json");
			StreamWriter configFile = File.CreateText (folder + "config.json");
			configFile.Write (this.jsonConfigFromMod (mod));
			configFile.Flush ();
			configFile.Close ();
			this.sortInstalledMods ();
		}




		public void enableMod(LocalMod mod) {
			mod.enabled = true;
			this.updateConfig (mod);
			ScrollsFilter.clearHooks ();

			String modLoaderPath = Platform.getModLoaderPath() + System.IO.Path.DirectorySeparatorChar;

			TypeDefinitionCollection types = AssemblyFactory.GetAssembly (modLoaderPath+"Assembly-CSharp.dll").MainModule.Types;
			loader.loadModsStatic (types);
			loader.addPatchHooks ();
			loader.loadMod (mod);
		}
		public void disableMod(LocalMod mod) {
			this.disableMod (mod, true);
		}
		public void disableMod(LocalMod mod, bool rebuild) {
			mod.enabled = false;
			this.updateConfig (mod);
			loader._unloadMod (mod);
			if (rebuild) {
				ScrollsFilter.clearHooks ();
				String modLoaderPath = Platform.getModLoaderPath() + System.IO.Path.DirectorySeparatorChar;
				TypeDefinitionCollection types = AssemblyFactory.GetAssembly (modLoaderPath+"Assembly-CSharp.dll").MainModule.Types;
				loader.loadModsStatic (types);
				loader.addPatchHooks ();
			}
		}




		public String jsonConfigFromMod(LocalMod mod) {
			JsonWriter writer = new JsonWriter ();
			return writer.Write (mod);
		}

		public string generateUniqueID() {
			bool searching = true;
			String newGuid = null;
			while (searching) {
				newGuid = Guid.NewGuid ().ToString ("N");
				searching = false;
				foreach (Item mod in installedMods)
					if ((mod as LocalMod).localId.Equals (newGuid))
						searching = true;
			}
			return newGuid;
		}
	}

	public class LocalMod : Mod {

		public bool localInstall;
		public String installPath;
		public String localId;
		public Repo source;
		public bool enabled;
		public bool queueForUninstall;

		public LocalMod(bool localInstall, String installPath, String localId, String serverId, Repo source, bool enabled, String name, String description, int version, String versionCode) {
			this.localId = localId;
			this.localInstall = localInstall;
			this.installPath = installPath;
			this.source = source;
			this.enabled = enabled;

			this.id = serverId;
			this.name = name;
			this.description = description;
			this.version = version;
			this.versionCode = versionCode;

			this.queueForUninstall = false;
		}

		public override int GetHashCode () {
			return localId.GetHashCode();
		}

		public override bool Equals(object mod) {
			if (mod is LocalMod) {
				return (mod as LocalMod).localId.Equals (this.localId);
			} else if (mod is Mod && !this.localInstall) {
				return (mod as Mod).id.Equals (this.id);
			} else
				return false;
		}
	}

	public class Mod : Item {

		public String id;
		public String name;
		public String description;
		public int version;
		public String versionCode;

		public bool selectable ()
		{
			return true;
		}
		public Texture getImage ()
		{
			return null;
		}
		public string getName ()
		{
			return name;
		}
		public string getDesc ()
		{
			return description+", "+versionCode;
		}

		public override bool Equals(object mod) {
			if (mod is Mod) {
				return (mod as Mod).id.Equals (this.id);
			} else
				return false;
		}

		public override int GetHashCode () {
			return id.GetHashCode();
		}
	}
}

