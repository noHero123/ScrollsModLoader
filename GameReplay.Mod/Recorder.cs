using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;

namespace GameReplay.Mod
{
	public class Recorder : ICommListener
	{
		private List<String> messages = new List<String>();
		private String saveFolder;
		private string gameID;
		//private DateTime timestamp;

		public Recorder (String saveFolder)
		{
			this.saveFolder = saveFolder;
			App.Communicator.addListener (this);
			//timestamp = DateTime.Now;
		}

		public void handleMessage (Message msg)
		{
			if (msg is BattleRedirectMessage || msg is BattleRejoinMessage || msg is FailMessage || msg is OkMessage)
				return;

			if (msg is GameInfoMessage) {
				gameID = (msg as GameInfoMessage).gameId.ToString();
			}

			messages.Add (msg.getRawText());

			if (msg is NewEffectsMessage && msg.getRawText().Contains("EndGame")) {
				//save
				File.WriteAllLines (saveFolder+Path.DirectorySeparatorChar+gameID+".sgr", messages.ToArray ());
			}

			if (msg is HandViewMessage)
				return;

			//TO-DO:
			//steaming
		}
		public void onReconnect ()
		{
			return; //I (still) don't care
		}
	}
}
