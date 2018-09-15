using System;
using System.Linq;
using System.Globalization;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Network;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;

namespace Oxide.Plugins
{
    [Info("WebShop", "contractjack", 0.1)]
    [Description("Private WebShop")]

    class WebShop : CovalencePlugin
    {
        void Init()
        {
            //cmd.AddChatCommand("get", this, "GetRequest");

        }
        
        [Command("claim")]
        void GetRequest(IPlayer player, string command, string[] args)
        {
            string steamid = player.Id;
            player.Reply("Checking WebShop....");
            webrequest.EnqueueGet("https://www.finvent.com/details.php?id=1", (code, response) => GetCallback(code, response, player), this);
        }

        void GetCallback(int code, string response, IPlayer player)
        {
            if (response == null || code != 200)
            {
                Puts($"Error: {code} - Couldn't get an answer from Google for {player.Name}");
                return;
            }
            
            if(response.Contains("Mr. Angelos Politis"))
            {
              player.Reply("Hi, " + player.Name);
              player.Reply(player.Id);
              //ConsoleSystem.Run(ConsoleSystem.Option.Server.Quiet(), "give supply.signal 10 " + player.Name);
            }
            else
            {
              player.Reply(player.Name);
              player.Reply(player.Id);            
            }

            //Puts($"Google answered for {player.Name}");
            //Puts("say hello world");
            //Puts($"{response}");
            
        }
    }
}


