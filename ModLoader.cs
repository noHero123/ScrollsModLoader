using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Diagnostics;
using LinFu.AOP.Interfaces;
using Mono.Cecil;
using ScrollsModLoader.Interfaces;
using UnityEngine;

namespace ScrollsModLoader {

	/*
	 * handels mod loading and debug file logging
	 * actual patches like logging network traffic, should be seperated in the future
	 * this class should only pass invokes through itself to the right mods
	 * 
	 * TO-DO: Add actual ModInterface ;)
	 * 
	 */

	public class ModInterceptor : IInterceptor
	{
		private ModLoader loader;

		public ModInterceptor(ModLoader loader) {
			this.loader = loader;
		}

		public object Intercept (IInvocationInfo info)
		{
			//old test
			//ModLoader.Log("Client: "+info.Arguments[0]);

			TypeDefinitionCollection types = AssemblyFactory.GetAssembly (Platform.getGlobalScrollsInstallPath()+"ModLoader/Assembly-CSharp.dll").MainModule.Types;
			Console.WriteLine (info.TargetMethod.Name);

			//we are loaded, get the hooks
			foreach (BaseMod mod in loader.GetModInstances()) {
				MethodDefinition[] requestedHooks = mod.GetHooks(types, SharedConstants.getGameVersion());
				if (requestedHooks.Where(item => item.Name.Equals(info.TargetMethod.Name) && item.DeclaringType.Name.Equals(info.TargetMethod.DeclaringType.Name)).Count() > 0) {
					object returnVal;
					if (mod.BeforeInvoke(new InvocationInfo(info), out returnVal)) {
						return returnVal;
					}
				}
			}

			object ret = info.TargetMethod.Invoke (info.Target, info.Arguments);

			foreach (BaseMod mod in loader.GetModInstances()) {
				MethodDefinition[] requestedHooks = mod.GetHooks (types, SharedConstants.getGameVersion ());
				if (requestedHooks.Where(item => item.Name.Equals(info.TargetMethod.Name) && item.DeclaringType.Name.Equals(info.TargetMethod.DeclaringType.Name)).Count() > 0) {
					mod.AfterInvoke (new InvocationInfo(info), ref ret);
				}
			}

			return ret;
		}
		
	}

	public class SimpleMethodReplacementProvider : BaseMethodReplacementProvider 
	{
		private ModLoader loader;

		public SimpleMethodReplacementProvider(ModLoader loader) {
			this.loader = loader;
		}

		protected override IInterceptor GetReplacement (object host, IInvocationInfo context)
		{
			return new ModInterceptor (loader);
		}
	}
	

	public class ModLoader
	{
		static bool init = false;
		static ModLoader instance = null;
		private List<String> modList = new List<String>();
		private List<BaseMod> modInstances = new List<BaseMod>();

		public ModLoader ()
		{
			System.Diagnostics.StackTrace t = new System.Diagnostics.StackTrace();
			Console.WriteLine (t);

			String installPath = Platform.getGlobalScrollsInstallPath();
			String modLoaderPath = installPath + "/ModLoader/";
			bool repatchNeeded = false;

			//load mod list
			if (!File.Exists (modLoaderPath+"mods.ini"))
				File.CreateText (modLoaderPath+"mods.ini").Close();
			modList = File.ReadAllLines (modLoaderPath+"mods.ini").ToList();

			//match it with mods avaiable
			if (!Directory.Exists (modLoaderPath+"mods"))
				Directory.CreateDirectory (modLoaderPath+"mods");
			String[] folderList = (from subdirectory in Directory.GetDirectories(modLoaderPath+"mods")
									where Directory.GetFiles(subdirectory, "*.mod.dll").Length != 0
									select subdirectory).ToArray();

			ResolveEventHandler resolver = new ResolveEventHandler(CurrentDomainOnAssemblyResolve);
			AppDomain.CurrentDomain.AssemblyResolve += resolver;

			//load mods
			foreach (String folder in folderList) {
				try {
					String[] modFiles = Directory.GetFiles (folder, "*.mod.dll");
					foreach (String modFile in modFiles) {
						Assembly mod = Assembly.LoadFile(modFile);
						//Assembly mod = AppDomain.CurrentDomain.Load(File.OpenRead(modFile).ReadToEnd());
						Type[] modClasses = (from modClass in mod.GetTypes ()
						                     where modClass.InheritsFrom (typeof(ScrollsModLoader.Interfaces.BaseMod))
						                     select modClass).ToArray();
						foreach (Type modClass in modClasses) {
							modInstances.Add((BaseMod)(modClass.GetConstructor (Type.EmptyTypes).Invoke (new object[0])));
						} 
					}
				} catch (ReflectionTypeLoadException exp) {
					Log (exp.ToString());
				}
			}

			//check mod list
			List<BaseMod> modInstancesCpy = new List<BaseMod>(modInstances);
			foreach (BaseMod mod in modInstancesCpy) {
				if (!modList.Contains (mod.GetName()+"."+mod.GetVersion())) {
					modList.Add (mod.GetName()+"."+mod.GetVersion());
					repatchNeeded = true;
					break;
				} else {
					//re-sort for mod order calling
					modInstances.Remove (mod);
					modInstances.Insert(modList.IndexOf(mod.GetName()+"."+mod.GetVersion()), mod);
				}
			}
			
			TypeDefinitionCollection types = AssemblyFactory.GetAssembly (modLoaderPath+"/Assembly-CSharp.dll").MainModule.Types;
			TypeDefinition[] typeArray = new TypeDefinition[types.Count];
			types.CopyTo(typeArray, 0);

			//we are loaded, get the hooks
			foreach (BaseMod mod in modInstancesCpy) {
				MethodDefinition[] requestedHooks = mod.GetHooks(types, SharedConstants.getGameVersion());

				//check hooks
				bool hooksAreValid = true;
				foreach (MethodDefinition hook in requestedHooks) {
					//type does not exists
					if ((from type in typeArray
					     where type.Equals(hook.DeclaringType)
					     select type).Count() == 0) {
						//disable mod
						modInstances.Remove (mod);
						hooksAreValid = false;
						break;
					}
				}
				if (!hooksAreValid)
					continue;

				//add hooks
				foreach (MethodDefinition hook in requestedHooks) {
					ScrollsFilter.ScrollsFilter.AddHook(hook);
				}
			}

			//repatch if necessary
			if (repatchNeeded) {
				
				Patcher patcher = new Patcher ();
				if (!patcher.patchAssembly ()) {
					//normal patching should never fail at this point
					//because this is no update and we are already patched
					//TO-DO get hook that crashed the patching and deactive mod instead
					Dialogs.showNotification ("Scrolls is broken", "Your Scrolls install appears to be broken or modified by other tools. ModLoader failed to load and will de-install itself");
					System.IO.File.Delete(installPath+"Assembly-CSharp.dll");
					System.IO.File.Copy(installPath+"ModLoader/Assembly-CSharp.dll", installPath+"Assembly-CSharp.dll");
					Application.Quit();
				}

				//restart the game
				if (Platform.getOS() == Platform.OS.Win)
				{
					new Process { StartInfo = { FileName = Platform.getGlobalScrollsInstallPath() + "/../../Scrolls.exe", Arguments = "" } }.Start();
					Application.Quit();
				}
				else if (Platform.getOS() == Platform.OS.Mac)
				{
					Process restart = new Process { StartInfo = { FileName = Platform.getGlobalScrollsInstallPath() + "/../../../../../run.sh", Arguments = "", UseShellExecute=true } };
					restart.Start();

					Application.Quit();
				} else {
					Application.Quit();
				}
			}

			//remove old mods from list
			foreach (String modDesc in modList.ToArray()) {
				bool loadedModFound = false;
				foreach (BaseMod mod in modInstances) {
					if (modDesc.Equals (mod.GetName()+"."+mod.GetVersion()))
						loadedModFound = true;
				}
				if (!loadedModFound)
					modList.Remove (modDesc);
			}

			//save ModList
			File.Delete (modLoaderPath+"mods.ini");
			StreamWriter modListWriter = File.CreateText (modLoaderPath+"mods.ini");
			foreach (String modDesc in modList) {
				modListWriter.WriteLine (modDesc);
			}
			modListWriter.Flush ();
			modListWriter.Close ();

			AppDomain.CurrentDomain.AssemblyResolve -= resolver;
		}

		private static System.Reflection.Assembly CurrentDomainOnAssemblyResolve(object sender, ResolveEventArgs args)
		{
			try {
				var asm = (from a in AppDomain.CurrentDomain.GetAssemblies()
				           where a.GetName().FullName == args.Name
				           select a).FirstOrDefault();
				Console.WriteLine(args.Name);
				if (asm == null) {
					asm = System.Reflection.Assembly.GetExecutingAssembly();
				}
				return asm;
			} catch (NullReferenceException exp) {
				Console.WriteLine(exp);
				//instance.Log (exp.ToString());
				return null;
			}
		}

		public BaseMod[] GetModInstances() {
			return modInstances.ToArray();
		}

		public void Log(String str) {
			UnityEngine.Debug.Log("MOD:"+str);
		}

		//initial game callback
		public static void Init() {

			//wiredly App.Awake() gets called multiple time, but we do not want multiple instances
			if (init)
				return;
			init = true;

			instance = new ModLoader();
			//old Test
			//App.Communicator.addListener(instance);
			MethodBodyReplacementProviderRegistry.SetProvider(new SimpleMethodReplacementProvider(instance));
			foreach (BaseMod mod in instance.modInstances) {
				mod.Init ();
			}
		}
	}
}