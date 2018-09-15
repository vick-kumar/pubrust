using System;
using System.Linq;
using System.Globalization;
using System.Collections.Generic;
using Oxide.Core;
using Oxide.Core.Configuration;
using Oxide.Game.Rust.Cui;
using Oxide.Core.Plugins;
using System.Reflection;
using UnityEngine;
using System.Text;

namespace Oxide.Plugins
{
    [Info("Admin Radar", "nivex", "4.4.0")]
    [Description("Radar tool for Admins and Developers.")]
    public class AdminRadar: RustPlugin
    {
        [PluginReference] Plugin Vanish;

        readonly string permName = "adminradar.allowed";
        static readonly string permBypass = "adminradar.bypass";
        static AdminRadar ins;
        DynamicConfigFile dataFile;
        static StoredData storedData = new StoredData();
        static bool init = false; // make sure the server is initialized otherwise OnEntitySpawned can throw errors
        static Dictionary<string, string> guiInfo = new Dictionary<string, string>();
        static List<string> tags = new List<string>() { "ore", "cluster", "1", "2", "3", "4", "5", "6", "_", ".", "-", "deployed", "wooden", "large", "pile", "prefab", "collectable", "loot", "small" }; // strip these from names to reduce the size of the text and make it more readable
        static Dictionary<ulong, int> drawnObjects = new Dictionary<ulong, int>();
        Dictionary<ulong, Color> playersColor = new Dictionary<ulong, Color>();
        static readonly List<ESP> activeRadars = new List<ESP>();
        static Dictionary<ulong, SortedDictionary<long, Vector3>> trackers = new Dictionary<ulong, SortedDictionary<long, Vector3>>(); // player id, timestamp and player's position
        static Dictionary<ulong, Timer> trackerTimers = new Dictionary<ulong, Timer>();
        static Dictionary<BasePlayer, PlayerData> players = new Dictionary<BasePlayer, PlayerData>();
        static List<ulong> invisible = new List<ulong>();
        static Cache cache = new Cache();
        const float flickerDelay = 0.05f;

        bool IsRadar(string id) => activeRadars.Any(x => x.player.UserIDString == id);
        static long TimeStamp() => (DateTime.Now.Ticks - DateTime.Parse("01/01/1970 00:00:00").Ticks) / 10000000;

        class Cache
        {
            public Dictionary<Vector3, CachedInfo> Bags = new Dictionary<Vector3, CachedInfo>();
            public Dictionary<Vector3, CachedInfo> Collectibles = new Dictionary<Vector3, CachedInfo>();
            public Dictionary<Vector3, CachedInfo> Containers = new Dictionary<Vector3, CachedInfo>();
            public Dictionary<Vector3, CachedInfo> Backpacks = new Dictionary<Vector3, CachedInfo>();
            public Dictionary<PlayerCorpse, CachedInfo> Corpses = new Dictionary<PlayerCorpse, CachedInfo>();
            public Dictionary<Vector3, CachedInfo> Ores = new Dictionary<Vector3, CachedInfo>();
            public Dictionary<Vector3, CachedInfo> TC = new Dictionary<Vector3, CachedInfo>();
            public Dictionary<Vector3, CachedInfo> Turrets = new Dictionary<Vector3, CachedInfo>();

            public List<BaseHelicopter> Helis = new List<BaseHelicopter>();
            public List<BasePlayer> NPC = new List<BasePlayer>();
            public List<BradleyAPC> Bradley = new List<BradleyAPC>();
            public List<SupplyDrop> Airdrop = new List<SupplyDrop>();
            public List<Zombie> Zombies = new List<Zombie>();

            public Cache() { }
        }

        class PlayerData
        {
            private long _lastVoiceTime;

            public bool IsVoice
            {
                get
                {
                    return TimeStamp() - _lastVoiceTime < voiceInterval;
                }
            }

            public long LastVoiceTime
            {
                set
                {
                    _lastVoiceTime = value;
                }
            }
        }

        class PlayerTracker : MonoBehaviour
        {
            BasePlayer player;
            ulong uid;

            void Awake()
            {
                player = GetComponent<BasePlayer>();
                uid = player.userID;
                InvokeRepeating("UpdateMovement", 0f, trackerUpdateInterval);
                UpdateMovement();
            }

            void UpdateMovement()
            {
                if (!player.IsConnected)
                {
                    GameObject.Destroy(this);
                    return;
                }

                if (!trackers.ContainsKey(uid))
                    trackers.Add(uid, new SortedDictionary<long, Vector3>());

                var currentStamp = TimeStamp();

                foreach (var stamp in trackers[uid].Keys.ToList()) // keep the dictionary from becoming enormous by removing entries which are too old
                    if (currentStamp - stamp > trackerAge)
                        trackers[uid].Remove(stamp);

                if (trackers[uid].Count > 1)
                {
                    var lastPos = trackers[uid].Values.ElementAt(trackers[uid].Count - 1); // get the last position the player was at

                    if (Vector3.Distance(lastPos, transform.position) <= 1f) // check the distance against the minimum requirement. without this the dictionary will accumulate thousands of entries
                        return;
                }

                trackers[uid][currentStamp] = transform.position;
                UpdateTimer();
            }

            void UpdateTimer()
            {
                if (trackerTimers.ContainsKey(uid))
                {
                    if (trackerTimers[uid] != null)
                    {
                        trackerTimers[uid].Reset();
                        return;
                    }
                    
                    trackerTimers.Remove(uid);
                }

                trackerTimers.Add(uid, ins.timer.Once(trackerAge, () =>
                {
                    if (trackers.ContainsKey(uid))
                        trackers.Remove(uid);

                    if (trackerTimers.ContainsKey(uid))
                        trackerTimers.Remove(uid);
                }));
            }

            void OnDestroy()
            {
                CancelInvoke("UpdateMovement");
                UpdateTimer();
                GameObject.Destroy(this);
            }
        }
        
        #region json 
        // TODO: Remove hardcoded json
        static string uiJson = @"[{
            ""name"": ""{guid}"",
            ""parent"": ""Hud"",
            ""components"": [
              {
                ""type"": ""UnityEngine.UI.Image"",
                ""color"": ""1 1 1 0""
              },
              {
                ""type"": ""RectTransform"",
                ""anchormin"": ""{anchorMin}"", 
                ""anchormax"": ""{anchorMax}""
              }
            ]
          },
          {
            ""name"": ""btnAll"",
            ""parent"": ""{guid}"",
            ""components"": [
              {
                ""type"": ""UnityEngine.UI.Button"",
                ""command"": ""espgui all"",
                ""close"": """",
                ""color"": ""0.29 0.49 0.69 0.5"",
              },
              {
                ""type"": ""RectTransform"",
                ""anchormin"": ""0.017 0.739"",
                ""anchormax"": ""0.331 0.957""
              }
            ]
          },
          {
            ""name"": ""lblAll"",
            ""parent"": ""btnAll"",
            ""components"": [
              {
                ""text"": ""All"",
                ""type"": ""UnityEngine.UI.Text"",
                ""color"": ""{colorAll}"",
                ""fontSize"": 10,
                ""align"": ""MiddleCenter""
              },
              {
                ""type"": ""RectTransform"",
                ""anchormin"": ""0 0"",
                ""anchormax"": ""1 1""
              }
            ]
          },
          {
            ""name"": ""btnBags"",
            ""parent"": ""{guid}"",
            ""components"": [
              {
                ""type"": ""UnityEngine.UI.Button"",
                ""command"": ""espgui bags"",
                ""close"": """",
                ""color"": ""0.29 0.49 0.69 0.5""
              },
              {
                ""type"": ""RectTransform"",
                ""anchormin"": ""0.017 0.5"",
                ""anchormax"": ""0.331 0.717""
              }
            ]
          },
          {
            ""name"": ""lblBags"",
            ""parent"": ""btnBags"",
            ""components"": [
              {
                ""text"": ""Bags"",
                ""type"": ""UnityEngine.UI.Text"",
                ""color"": ""{colorBags}"",
                ""fontSize"": 10,
                ""align"": ""MiddleCenter""
              },
              {
                ""type"": ""RectTransform"",
                ""anchormin"": ""0 0"",
                ""anchormax"": ""1 1""
              }
            ]
          },
          {
            ""name"": ""btnBox"",
            ""parent"": ""{guid}"",
            ""components"": [
              {
                ""type"": ""UnityEngine.UI.Button"",
                ""command"": ""espgui box"",
                ""close"": """",
                ""color"": ""0.29 0.49 0.69 0.5""
              },
              {
                ""type"": ""RectTransform"",
                ""anchormin"": ""0.017 0.261"",
                ""anchormax"": ""0.331 0.478""
              }
            ]
          },
          {
            ""name"": ""lblBox"",
            ""parent"": ""btnBox"",
            ""components"": [
              {
                ""text"": ""Boxes"",
                ""type"": ""UnityEngine.UI.Text"",
                ""color"": ""{colorBox}"",
                ""fontSize"": 10,
                ""align"": ""MiddleCenter""
              },
              {
                ""type"": ""RectTransform"",
                ""anchormin"": ""0 0"",
                ""anchormax"": ""1 1""
              }
            ]
          },
          {
            ""name"": ""btnCollectables"",
            ""parent"": ""{guid}"",
            ""components"": [
              {
                ""type"": ""UnityEngine.UI.Button"",
                ""command"": ""espgui col"",
                ""close"": """",
                ""color"": ""0.29 0.49 0.69 0.5""
              },
              {
                ""type"": ""RectTransform"",
                ""anchormin"": ""0.017 0.022"",
                ""anchormax"": ""0.331 0.239""
              }
            ]
          },
          {
            ""name"": ""lblCollectables"",
            ""parent"": ""btnCollectables"",
            ""components"": [
              {
                ""text"": ""Collectibles"",
                ""type"": ""UnityEngine.UI.Text"",
                ""color"": ""{colorCol}"",
                ""fontSize"": 10,
                ""align"": ""MiddleCenter""
              },
              {
                ""type"": ""RectTransform"",
                ""anchormin"": ""0 0"",
                ""anchormax"": ""1 1""
              }
            ]
          },
          {
            ""name"": ""btnDead"",
            ""parent"": ""{guid}"",
            ""components"": [
              {
                ""type"": ""UnityEngine.UI.Button"",
                ""command"": ""espgui dead"",
                ""close"": """",
                ""color"": ""0.29 0.49 0.69 0.5""
              },
              {
                ""type"": ""RectTransform"",
                ""anchormin"": ""0.343 0.739"",
                ""anchormax"": ""0.657 0.957""
              }
            ]
          },
          {
            ""name"": ""lblDead"",
            ""parent"": ""btnDead"",
            ""components"": [
              {
                ""text"": ""Dead"",
                ""type"": ""UnityEngine.UI.Text"",
                ""color"": ""{colorDead}"",
                ""fontSize"": 9,
                ""align"": ""MiddleCenter""
              },
              {
                ""type"": ""RectTransform"",
                ""anchormin"": ""0 0"",
                ""anchormax"": ""1 1""
              }
            ]
          },
          {
            ""name"": ""btnLoot"",
            ""parent"": ""{guid}"",
            ""components"": [
              {
                ""type"": ""UnityEngine.UI.Button"",
                ""command"": ""espgui loot"",
                ""close"": """",
                ""color"": ""0.29 0.49 0.69 0.5""
              },
              {
                ""type"": ""RectTransform"",
                ""anchormin"": ""0.343 0.5"",
                ""anchormax"": ""0.657 0.717""
              }
            ]
          },
          {
            ""name"": ""lblLoot"",
            ""parent"": ""btnLoot"",
            ""components"": [
              {
                ""text"": ""Loot"",
                ""type"": ""UnityEngine.UI.Text"",
                ""color"": ""{colorLoot}"",
                ""fontSize"": 10,
                ""align"": ""MiddleCenter""
              },
              {
                ""type"": ""RectTransform"",
                ""anchormin"": ""0 0"",
                ""anchormax"": ""1 1""
              }
            ]
          },
          {
            ""name"": ""btnNPC"",
            ""parent"": ""{guid}"",
            ""components"": [
              {
                ""type"": ""UnityEngine.UI.Button"",
                ""command"": ""espgui npc"",
                ""close"": """",
                ""color"": ""0.29 0.49 0.69 0.5""
              },
              {
                ""type"": ""RectTransform"",
                ""anchormin"": ""0.343 0.261"",
                ""anchormax"": ""0.657 0.478""
              }
            ]
          },
          {
            ""name"": ""lblNPC"",
            ""parent"": ""btnNPC"",
            ""components"": [
              {
                ""text"": ""NPC"",
                ""type"": ""UnityEngine.UI.Text"",
                ""color"": ""{colorNPC}"",
                ""fontSize"": 10,
                ""align"": ""MiddleCenter""
              },
              {
                ""type"": ""RectTransform"",
                ""anchormin"": ""0 0"",
                ""anchormax"": ""1 1""
              }
            ]
          },
          {
            ""name"": ""btnOre"",
            ""parent"": ""{guid}"",
            ""components"": [
              {
                ""type"": ""UnityEngine.UI.Button"",
                ""command"": ""espgui ore"",
                ""close"": """",
                ""color"": ""0.29 0.49 0.69 0.5""
              },
              {
                ""type"": ""RectTransform"",
                ""anchormin"": ""0.343 0.022"",
                ""anchormax"": ""0.657 0.239""
              }
            ]
          },
          {
            ""name"": ""lblOre"",
            ""parent"": ""btnOre"",
            ""components"": [
              {
                ""text"": ""Ore"",
                ""type"": ""UnityEngine.UI.Text"",
                ""color"": ""{colorOre}"",
                ""fontSize"": 10,
                ""align"": ""MiddleCenter""
              },
              {
                ""type"": ""RectTransform"",
                ""anchormin"": ""0 0"",
                ""anchormax"": ""1 1""
              }
            ]
          },
          {
            ""name"": ""btnSleepers"",
            ""parent"": ""{guid}"",
            ""components"": [
              {
                ""type"": ""UnityEngine.UI.Button"",
                ""command"": ""espgui sleepers"",
                ""close"": """",
                ""color"": ""0.29 0.49 0.69 0.5""
              },
              {
                ""type"": ""RectTransform"",
                ""anchormin"": ""0.669 0.739"",
                ""anchormax"": ""0.984 0.957""
              }
            ]
          },
          {
            ""name"": ""lblSleepers"",
            ""parent"": ""btnSleepers"",
            ""components"": [
              {
                ""text"": ""Sleepers"",
                ""type"": ""UnityEngine.UI.Text"",
                ""color"": ""{colorSleepers}"",
                ""fontSize"": 10,
                ""align"": ""MiddleCenter""
              },
              {
                ""type"": ""RectTransform"",
                ""anchormin"": ""0 0"",
                ""anchormax"": ""1 1""
              }
            ]
          },
          {
            ""name"": ""btnStash"",
            ""parent"": ""{guid}"",
            ""components"": [
              {
                ""type"": ""UnityEngine.UI.Button"",
                ""command"": ""espgui stash"",
                ""close"": """",
                ""color"": ""0.29 0.49 0.69 0.5""
              },
              {
                ""type"": ""RectTransform"",
                ""anchormin"": ""0.669 0.5"",
                ""anchormax"": ""0.984 0.717""
              }
            ]
          },
          {
            ""name"": ""lblStash"",
            ""parent"": ""btnStash"",
            ""components"": [
              {
                ""text"": ""Stash"",
                ""type"": ""UnityEngine.UI.Text"",
                ""color"": ""{colorStash}"",
                ""fontSize"": 10,
                ""align"": ""MiddleCenter""
              },
              {
                ""type"": ""RectTransform"",
                ""anchormin"": ""0 0"",
                ""anchormax"": ""1 1""
              }
            ]
          },
          {
            ""name"": ""btnTC"",
            ""parent"": ""{guid}"",
            ""components"": [
              {
                ""type"": ""UnityEngine.UI.Button"",
                ""command"": ""espgui tc"",
                ""close"": """",
                ""color"": ""0.29 0.49 0.69 0.5""
              },
              {
                ""type"": ""RectTransform"",
                ""anchormin"": ""0.669 0.261"",
                ""anchormax"": ""0.984 0.478""
              }
            ]
          },
          {
            ""name"": ""lblTC"",
            ""parent"": ""btnTC"",
            ""components"": [
              {
                ""text"": ""TC"",
                ""type"": ""UnityEngine.UI.Text"",
                ""color"": ""{colorTC}"",
                ""fontSize"": 10,
                ""align"": ""MiddleCenter""
              },
              {
                ""type"": ""RectTransform"",
                ""anchormin"": ""0 0"",
                ""anchormax"": ""1 1""
              }
            ]
          },
          {
            ""name"": ""btnTurrets"",
            ""parent"": ""{guid}"",
            ""components"": [
              {
                ""type"": ""UnityEngine.UI.Button"",
                ""command"": ""espgui turrets"",
                ""close"": """",
                ""color"": ""0.29 0.49 0.69 0.5""
              },
              {
                ""type"": ""RectTransform"",
                ""anchormin"": ""0.669 0.022"",
                ""anchormax"": ""0.984 0.239""
              }
            ]
          },
          {
            ""name"": ""lblTurrets"",
            ""parent"": ""btnTurrets"",
            ""components"": [
              {
                ""text"": ""Turrets"",
                ""type"": ""UnityEngine.UI.Text"",
                ""color"": ""{colorTurrets}"",
                ""fontSize"": 10,
                ""align"": ""MiddleCenter""
              },
              {
                ""type"": ""RectTransform"",
                ""anchormin"": ""0 0"",
                ""anchormax"": ""1 1""
              }
            ]
          }
        ]";
        #endregion

        void Init()
        {
            ins = this;
        }

        void Loaded()
        {
            cache = new Cache();
            permission.RegisterPermission(permName, this);
            permission.RegisterPermission(permBypass, this);
        }

        void OnServerInitialized()
        {            
            dataFile = Interface.Oxide.DataFileSystem.GetFile(Name);

            try
            {
                storedData = dataFile.ReadObject<StoredData>();
            }
            catch { }
            
            if (storedData == null)
                storedData = new StoredData();

            LoadVariables();

            if (!usePlayerTracker)
                Unsubscribe(nameof(OnPlayerSleepEnded));
            else
                foreach (var target in BasePlayer.activePlayerList)
                    Track(target);

            if (!drawBox && !drawText && !drawArrows)
            {
                Puts("Configuration does not have a chosen drawing method. Setting drawing method to text.");
                Config.Set("Drawing Methods", "Draw Text", true);
                Config.Save();
                drawText = true;
            }

            if (!useVoiceDetection)
            {
                Unsubscribe(nameof(OnPlayerVoice));
                Unsubscribe(nameof(OnPlayerDisconnected));
            }

            var tick = DateTime.Now;
            init = true;

            if (!playerOnlyMode)
            {
                int cached = 0, total = 0;
                foreach (var e in BaseNetworkable.serverEntities)
                {
                    if (AddToCache(e))
                        cached++;

                    total++;
                }

                //Puts("Took {0}ms to cache {1}/{2} entities: {3} bags, {4} collectibles, {5} containers, {6} corpses, {7} ores, {8} tool cupboards, {9} turrets.", (DateTime.Now - tick).TotalMilliseconds, cached, total, cachedBags.Count, cachedCollectibles.Count, cachedContainers.Count, cachedCorpses.Count, cachedOres.Count, cachedTC.Count, cachedTurrets.Count);
                Puts("Took {0}ms to cache {1}/{2} entities", (DateTime.Now - tick).TotalMilliseconds, cached, total);
            }

            SaveConfig();
            invisible.Clear();

            if (Vanish != null)
            {
                foreach (var player in BasePlayer.activePlayerList.Where(x => x != null && x.IsConnected))
                {
                    var o = Vanish?.Call("IsInvisible", player);

                    if (o != null && o is bool && (bool)o)
                    {
                        invisible.Add(player.userID);
                    }
                }
            }
        }

        bool playerOnlyMode = false;

        void OnPlayerSleepEnded(BasePlayer player)
        {
            if (!trackAdmins && player.IsAdmin)
                return;

            Track(player);
        }

        void OnPlayerVoice(BasePlayer player, Byte[] data)
        {
            if (!players.ContainsKey(player))
                players.Add(player, new PlayerData());

            players[player].LastVoiceTime = TimeStamp();
        }

        void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            players.Remove(player);
        }

        void Unload()
        {
            players.Clear();

            var espobjects = UnityEngine.Object.FindObjectsOfType(typeof(ESP));

            if (espobjects != null)
                foreach (var gameObj in espobjects)
                    UnityEngine.Object.Destroy(gameObj);

            var ptobjects = UnityEngine.Object.FindObjectsOfType(typeof(PlayerTracker));

            if (ptobjects != null)
                foreach (var gameObj in ptobjects)
                    UnityEngine.Object.Destroy(gameObj);

            if (dataFile != null)
                dataFile.WriteObject(storedData);

            foreach(var entry in trackerTimers.ToList())
                if (entry.Value != null && !entry.Value.Destroyed)
                    entry.Value.Destroy();

            playersColor.Clear();
            trackerTimers.Clear();
            trackers.Clear();
            drawnObjects?.Clear();
            cache = null;
            tags?.Clear();
        }

        void OnEntityDeath(BaseCombatEntity entity, HitInfo info) => RemoveFromCache(entity);
        void OnEntityKill(BaseNetworkable entity) => RemoveFromCache(entity);
        void OnEntitySpawned(BaseNetworkable entity) => AddToCache(entity.GetComponent<BaseEntity>());

        private void OnVanishDisappear(BasePlayer player)
        {
            if (player != null && !invisible.Contains(player.userID))
            {
                invisible.Add(player.userID);
            }
        }

        private void OnVanishReappear(BasePlayer player)
        {
            if (player != null)
            {
                invisible.Remove(player.userID);
            }
        }

        class StoredData
        {
            public List<string> Visions = new List<string>();
            public List<string> OnlineBoxes = new List<string>();
            public Dictionary<string, List<string>> Filters = new Dictionary<string, List<string>>();
            public List<string> Hidden = new List<string>();
            public List<string> Extended = new List<string>();
            public StoredData() { }
        }

        class CachedInfo
        {
            public string Name;
            public object Info;
            public double Size;
            public CachedInfo() { }
        }

        class ESP : MonoBehaviour
        {
            public BasePlayer player;
            BaseEntity source;
            public float maxDistance;
            public float invokeTime;
            private float inactiveTime;
            private int inactiveMins;
            private Vector3 position;

            public bool showAll;
            public bool showBags;
            public bool showBox;
            public bool showCollectible;
            public bool showDead;
            public bool showLoot;
            public bool showNPC;
            public bool showOre;
            public bool showSleepers;
            public bool showStash;
            public bool showTC;
            public bool showTurrets;

            private List<BasePlayer> activePlayers = new List<BasePlayer>();

            void Awake()
            {
                player = GetComponent<BasePlayer>();
                source = player;
                position = player.transform.position;

                if (inactiveTimeLimit > 0f || deactiveTimeLimit > 0)
                    InvokeHandler.InvokeRepeating(this, Activity, 0f, 1f);
            }

            void OnDestroy()
            {
                string gui;
                if (guiInfo.TryGetValue(player.UserIDString, out gui))
                {
                    CuiHelper.DestroyUi(player, gui);
                    guiInfo.Remove(player.UserIDString);
                }

                if (inactiveTimeLimit > 0f || deactiveTimeLimit > 0)
                    InvokeHandler.CancelInvoke(this, Activity);

                activeRadars.Remove(this);
                player.ChatMessage(ins.msg("Deactivated", player.UserIDString));                
                GameObject.Destroy(this);
            }

            bool LatencyAccepted(DateTime tick)
            {
                if (latencyMs > 0)
                {
                    var ms = (DateTime.Now - tick).TotalMilliseconds;

                    if (ms > latencyMs)
                    {
                        player.ChatMessage(ins.msg("DoESP", player.UserIDString, ms, latencyMs));
                        return false;
                    }
                }

                return true;
            }

            void Activity()
            {
                if (source != player)
                    return;

                inactiveTime = position == player.transform.position ? inactiveTime + 1f : 0f;
                position = player.transform.position;

                if (inactiveTimeLimit > 0f && inactiveTime > inactiveTimeLimit)
                    GameObject.Destroy(this);

                if (deactiveTimeLimit > 0)
                {
                    if (inactiveTime > 0f && inactiveTime % 60 == 0)
                        inactiveMins++;
                    else
                        inactiveMins = 0;

                    if (inactiveMins >= deactiveTimeLimit)
                        GameObject.Destroy(this);
                }
            }

            void DoESP()
            {
                var tick = DateTime.Now;
                string error = "TRY";

                try
                {
                    error = "PLAYER";
                    if (!player.IsConnected)
                    {
                        GameObject.Destroy(this);
                        return;
                    }

                    error = "SOURCE";
                    source = player.IsSpectating() ? player.GetParentEntity() : player;

                    if (!(source is BasePlayer)) // compatibility for HideAndSeek plugin otherwise exceptions will be thrown
                        source = player;

                    if (player == source && (player.IsDead() || player.IsSleeping() || player.HasPlayerFlag(BasePlayer.PlayerFlags.ReceivingSnapshot)))
                        return;

                    drawnObjects[player.userID] = 0;

                    error = "HELI";
                    if (trackHelis && cache.Helis.Count > 0)
                    {
                        foreach (var heli in cache.Helis)
                        {
                            if (heli == null || heli.transform == null)
                                continue;

                            double currDistance = Math.Floor(Vector3.Distance(heli.transform.position, source.transform.position));
                            string heliHealth = heli.health > 1000 ? Math.Floor(heli.health).ToString("#,##0,K", CultureInfo.InvariantCulture) : Math.Floor(heli.health).ToString();
                            string info = showHeliRotorHealth ? string.Format("<color={0}>{1}</color> (<color=yellow>{2}</color>/<color=yellow>{3}</color>)", healthCC, heliHealth, Math.Floor(heli.weakspots[0].health), Math.Floor(heli.weakspots[1].health)) : string.Format("<color={0}>{1}</color>", healthCC, heliHealth);
                            
                            if (drawText) player.SendConsoleCommand("ddraw.text", invokeTime + flickerDelay, __(heliCC), heli.transform.position + new Vector3(0f, 2f, 0f), string.Format("H {0} <color={1}>{2}</color>", info, distCC, currDistance));
                            if (drawBox) player.SendConsoleCommand("ddraw.box", invokeTime + flickerDelay, __(heliCC), heli.transform.position + new Vector3(0f, 1f, 0f), GetScale(currDistance));
                        }
                    }

                    if (!LatencyAccepted(tick)) // causing server lag, return. this shouldn't happen unless the server is already experiencing latency issues
                        return;

                    error = "BRADLEY";
                    if (trackHelis && cache.Bradley.Count > 0)
                    {
                        foreach (var bradley in cache.Bradley)
                        {
                            if (bradley == null || bradley.transform == null)
                                continue;

                            double currDistance = Math.Floor(Vector3.Distance(bradley.transform.position, source.transform.position));
                            string info = string.Format("<color={0}>{1}</color>", healthCC, bradley.health > 1000 ? Math.Floor(bradley.health).ToString("#,##0,K", CultureInfo.InvariantCulture) : Math.Floor(bradley.health).ToString());

                            if (drawText) player.SendConsoleCommand("ddraw.text", invokeTime + flickerDelay, __(bradleyCC), bradley.transform.position + new Vector3(0f, 2f, 0f), string.Format("B {0} <color={1}>{2}</color>", info, distCC, currDistance));
                            if (drawBox) player.SendConsoleCommand("ddraw.box", invokeTime + flickerDelay, __(bradleyCC), bradley.transform.position + new Vector3(0f, 1f, 0f), GetScale(currDistance));
                        }
                    }

                    error = "ACTIVE";
                    foreach (var target in BasePlayer.activePlayerList.Where(target => target != null && target.transform != null && target.IsConnected))
                    {
                        double currDistance = Math.Floor(Vector3.Distance(target.transform.position, source.transform.position));

                        if (player == target || currDistance > maxDistance || (useBypass && ins.permission.UserHasPermission(target.UserIDString, permBypass)))
                            continue;

                        var color = __(target.IsAlive() ? activeCC : activeDeadCC);

                        if (currDistance < playerDistance)
                        {
                            string extText = string.Empty;

                            if (storedData.Extended.Contains(player.UserIDString))
                            {
                                extText = target.GetActiveItem()?.info.displayName.translated ?? string.Empty;

                                if (!string.IsNullOrEmpty(extText))
                                {
                                    var itemList = target?.GetHeldEntity()?.GetComponent<BaseProjectile>()?.GetItem()?.contents?.itemList;

                                    if (itemList?.Count > 0)
                                    {
                                        string contents = string.Join("|", itemList.Select(item => item.info.displayName.translated.Replace("Weapon ", "").Replace("Simple Handmade ", "").Replace("Muzzle ", "").Replace("4x Zoom Scope", "4x")).ToArray());

                                        if (!string.IsNullOrEmpty(contents))
                                            extText = string.Format("{0} ({1})", extText, contents);
                                    }
                                }
                            }

                            string vanished = invisible.Contains(target.userID) ? "<color=magenta>V</color>" : string.Empty;
                            
                            if (storedData.Visions.Contains(player.UserIDString)) DrawVision(player, target, invokeTime);
                            if (drawArrows) player.SendConsoleCommand("ddraw.arrow", invokeTime + flickerDelay, color, target.transform.position + new Vector3(0f, target.transform.position.y + 10), target.transform.position, 1);
                            if (drawText) player.SendConsoleCommand("ddraw.text", invokeTime + flickerDelay, color, target.transform.position + new Vector3(0f, 2f, 0f), string.Format("{0} <color={1}>{2}</color> <color={3}>{4}</color>{5} {6}", target.displayName ?? target.userID.ToString(), healthCC, Math.Floor(target.health), distCC, currDistance, vanished, extText));
                            if (drawBox) player.SendConsoleCommand("ddraw.box", invokeTime + flickerDelay, color, target.transform.position + new Vector3(0f, 1f, 0f), target.GetHeight(target.modelState.ducked));
                            if (useVoiceDetection && players.ContainsKey(target) && players[target].IsVoice && Vector3.Distance(target.transform.position, player.transform.position) <= voiceDistance)
                            {
                                player.SendConsoleCommand("ddraw.arrow", invokeTime + flickerDelay, Color.yellow, target.transform.position + new Vector3(0f, 2.5f, 0f), target.transform.position, 1); 
                            }
                        }
                        else if (drawX)
                            activePlayers.Add(target);
                        else
                            player.SendConsoleCommand("ddraw.box", invokeTime + flickerDelay, color, target.transform.position + new Vector3(0f, 1f, 0f), 5f);

                        if (objectsLimit > 0 && ++drawnObjects[player.userID] > objectsLimit) return;
                    }

                    error = "GROUP LIMIT HIGHLIGHTING";
                    if (activePlayers.Count > 0)
                    {
                        var dict = new Dictionary<int, List<BasePlayer>>();

                        foreach(var target in activePlayers.ToList())
                        {
                            var list = activePlayers.Where(x => Vector3.Distance(x.transform.position, target.transform.position) < groupRange && !dict.Any(y => y.Value.Contains(x))).ToList();

                            if (list.Count() >= groupLimit)
                            {
                                int index = 0;

                                while (dict.ContainsKey(index))
                                    index++;

                                dict.Add(index, list);
                                activePlayers.RemoveAll(x => list.Contains(x));
                            }
                        }

                        foreach (var target in activePlayers)
                            player.SendConsoleCommand("ddraw.text", invokeTime + flickerDelay, target.IsAlive() ? Color.green : Color.red, target.transform.position + new Vector3(0f, 1f, 0f), "X");

                        int group = 0;

                        foreach (var entry in dict)
                        {
                            foreach (var target in entry.Value)
                            {
                                player.SendConsoleCommand("ddraw.text", invokeTime + flickerDelay, __(target.IsAlive() ? GetGroupColor(group) : groupColorDead), target.transform.position + new Vector3(0f, 1f, 0f), "X");
                            }

                            if (groupCountHeight > 0f)
                            {
                                player.SendConsoleCommand("ddraw.text", invokeTime + flickerDelay, Color.magenta, entry.Value.First().transform.position + new Vector3(0f, groupCountHeight, 0f), entry.Value.Count.ToString());
                            }
                        }

                        activePlayers.Clear();
                        dict.Clear();
                    }

                    if (!LatencyAccepted(tick))
                        return;

                    error = "TC";
                    if (showTC || showAll)
                    {
                        foreach (var tc in cache.TC)
                        {
                            double currDistance = Math.Floor(Vector3.Distance(tc.Key, source.transform.position));

                            if (currDistance < tcDistance && currDistance < maxDistance)
                            {
                                if (drawText) player.SendConsoleCommand("ddraw.text", invokeTime + flickerDelay, __(tcCC), tc.Key + new Vector3(0f, 0.5f, 0f), string.Format("TC <color={0}>{1}</color>", distCC, currDistance));
                                if (drawBox) player.SendConsoleCommand("ddraw.box", invokeTime + flickerDelay, __(tcCC), tc.Key + new Vector3(0f, 0.5f, 0f), tc.Value.Size);
                                if (objectsLimit > 0 && ++drawnObjects[player.userID] > objectsLimit) return;
                            }
                        }
                    }

                    if (!LatencyAccepted(tick))
                        return;

                    if (showBox || showLoot || showStash || showAll)
                    {
                        if (showLoot || showAll)
                        {
                            error = "BACKPACKS";

                            foreach (var entry in cache.Backpacks)
                            {
                                double currDistance = Math.Floor(Vector3.Distance(entry.Key, source.transform.position));

                                if (currDistance > maxDistance)
                                    continue;

                                if (currDistance < lootDistance)
                                {
                                    string contents = string.Empty;
                                    uint uid;

                                    if (entry.Value.Info != null && uint.TryParse(entry.Value.Info.ToString(), out uid))
                                    {
                                        var backpack = BaseNetworkable.serverEntities.Find(uid) as DroppedItemContainer;

                                        if (backpack == null)
                                            continue;

                                        if (backpack.inventory?.itemList != null) contents = string.Format("({0}) ", backpackContentAmount > 0 && backpack.inventory.itemList.Count > 0 ? string.Join(", ", backpack.inventory.itemList.Take(backpackContentAmount).Select(item => string.Format("{0} ({1})", item.info.displayName.translated.ToLower(), item.amount)).ToArray()) : backpack.inventory.itemList.Count().ToString());
                                        if (drawText) player.SendConsoleCommand("ddraw.text", invokeTime + flickerDelay, __(backpackCC), entry.Key + new Vector3(0f, 0.5f, 0f), string.Format("{0} <color={1}>{2}</color><color={3}>{4}</color>", string.IsNullOrEmpty(backpack._playerName) ? ins.msg("backpack", player.UserIDString) : backpack._playerName, backpackCC, contents, distCC, currDistance));
                                        if (drawBox) player.SendConsoleCommand("ddraw.box", invokeTime + flickerDelay, __(backpackCC), entry.Key + new Vector3(0f, 0.5f, 0f), GetScale(currDistance));
                                        if (objectsLimit > 0 && ++drawnObjects[player.userID] > objectsLimit) return;
                                    }
                                }
                            }
                        }

                        if (showBox || showAll)
                        {
                            error = "AIRDROPS";
                            foreach (var drop in cache.Airdrop)
                            {
                                double currDistance = Math.Floor(Vector3.Distance(drop.transform.position, source.transform.position));

                                if (currDistance > maxDistance || currDistance > adDistance)
                                    continue;

                                string contents = showAirdropContents && drop.inventory.itemList.Count > 0 ? string.Format("({0}) ", string.Join(", ", drop.inventory.itemList.Select(item => string.Format("{0} ({1})", item.info.displayName.translated.ToLower(), item.amount)).ToArray())) : string.Format("({0}) ", drop.inventory.itemList.Count());
                                
                                if (drawText) player.SendConsoleCommand("ddraw.text", invokeTime + flickerDelay, __(airdropCC), drop.transform.position + new Vector3(0f, 0.5f, 0f), string.Format("{0} {1}<color={2}>{3}</color>", _(drop.ShortPrefabName), contents, distCC, currDistance));
                                if (drawBox) player.SendConsoleCommand("ddraw.box", invokeTime + flickerDelay, __(airdropCC), drop.transform.position + new Vector3(0f, 0.5f, 0f), GetScale(currDistance));
                            }
                        }

                        error = "CONTAINERS";
                        foreach (var box in cache.Containers)
                        {
                            double currDistance = Math.Floor(Vector3.Distance(box.Key, source.transform.position));

                            if (currDistance > maxDistance)
                                continue;

                            bool isBox = (box.Value.Name.Contains("box") || box.Value.Name.Equals("heli_crate"));
                            bool isLoot = box.Value.Name.Contains("loot") || box.Value.Name.Contains("crate_") || box.Value.Name.Contains("trash");
                            
                            if (isBox)
                            {
                                if (!showBox && !showAll)
                                    continue;

                                if (currDistance > boxDistance)
                                    continue;
                            }

                            if (isLoot)
                            {
                                if (!showLoot && !showAll)
                                    continue;

                                if (currDistance > lootDistance)
                                    continue;
                            }

                            if (box.Value.Name.Contains("stash"))
                            {
                                if (!showStash && !showAll)
                                    continue;

                                if (currDistance > stashDistance)
                                    continue;
                            }

                            var color = isBox ? Color.magenta : isLoot ? Color.yellow : Color.white;
                            string colorHex = color == Color.magenta ? boxCC : color == Color.yellow ? lootCC: stashCC;

                            string contents = string.Empty;
                            uint uid;

                            if (box.Value.Info != null && uint.TryParse(box.Value.Info.ToString(), out uid))
                            {
                                var container = BaseNetworkable.serverEntities.Find(uid) as StorageContainer;

                                if (container == null)
                                    continue;

                                if (storedData.OnlineBoxes.Contains(player.UserIDString) && container.name.Contains("box"))
                                {
                                    var owner = BasePlayer.activePlayerList.Find(x => x.userID == container.OwnerID);

                                    if (owner == null || !owner.IsConnected)
                                    {
                                        continue;
                                    }
                                }

                                if (container?.inventory?.itemList != null)
                                {
                                    if (container.inventory.itemList.Count > 0)
                                    {
                                        if ((isLoot && showLootContents) || (container.ShortPrefabName.Contains("stash") && showStashContents))
                                            contents = string.Format("({0}) ", string.Join(", ", container.inventory.itemList.Select(item => string.Format("{0} ({1})", item.info.displayName.translated.ToLower(), item.amount)).ToArray()));
                                        else
                                            contents = string.Format("({0}) ", container.inventory.itemList.Count());
                                    }
                                }
                            }

                            if (string.IsNullOrEmpty(contents) && !drawEmptyContainers)
                                continue;

                            if (drawText) player.SendConsoleCommand("ddraw.text", invokeTime + flickerDelay, __(colorHex), box.Key + new Vector3(0f, 0.5f, 0f), string.Format("{0} {1}<color={2}>{3}</color>", _(box.Value.Name), contents, distCC, currDistance));
                            if (drawBox) player.SendConsoleCommand("ddraw.box", invokeTime + flickerDelay, __(colorHex), box.Key + new Vector3(0f, 0.5f, 0f), GetScale(currDistance));
                            if (objectsLimit > 0 && ++drawnObjects[player.userID] > objectsLimit) return;
                        }
                    }

                    if (!LatencyAccepted(tick))
                        return;

                    error = "BAGS";
                    if (showBags || showAll)
                    {
                        foreach (var bag in cache.Bags)
                        {
                            var currDistance = Math.Floor(Vector3.Distance(bag.Key, source.transform.position));

                            if (currDistance < bagDistance && currDistance < maxDistance)
                            {
                                if (drawText) player.SendConsoleCommand("ddraw.text", invokeTime + flickerDelay, __(bagCC), bag.Key, string.Format("bag <color={0}>{1}</color>", distCC, currDistance));
                                if (drawBox) player.SendConsoleCommand("ddraw.box", invokeTime + flickerDelay, __(bagCC), bag.Key, bag.Value.Size);
                                if (objectsLimit > 0 && ++drawnObjects[player.userID] > objectsLimit) return;
                            }
                        }
                    }

                    if (!LatencyAccepted(tick))
                        return;

                    error = "TURRETS";
                    if (showTurrets || showAll)
                    {
                        foreach (var turret in cache.Turrets)
                        {
                            var currDistance = Math.Floor(Vector3.Distance(turret.Key, source.transform.position));

                            if (currDistance < turretDistance && currDistance < maxDistance)
                            {
                                if (drawText) player.SendConsoleCommand("ddraw.text", invokeTime + flickerDelay, __(atCC), turret.Key + new Vector3(0f, 0.5f, 0f), string.Format("AT ({0}) <color={1}>{2}</color>", turret.Value.Info, distCC, currDistance));
                                if (drawBox) player.SendConsoleCommand("ddraw.box", invokeTime + flickerDelay, __(atCC), turret.Key + new Vector3(0f, 0.5f, 0f), turret.Value.Size);
                                if (objectsLimit > 0 && ++drawnObjects[player.userID] > objectsLimit) return;
                            }
                        }
                    }

                    if (!LatencyAccepted(tick))
                        return;

                    error = "SLEEPERS";
                    if (showSleepers || showAll)
                    {
                        foreach (var sleeper in BasePlayer.sleepingPlayerList)
                        {
                            if (sleeper == null || sleeper.transform == null)
                                continue;

                            double currDistance = Math.Floor(Vector3.Distance(sleeper.transform.position, source.transform.position));

                            if (currDistance > maxDistance)
                                continue;

                            if (currDistance < playerDistance)
                            {
                                string vanished = invisible.Contains(sleeper.userID) ? "<color=magenta>V</color>" : string.Empty;
                                var color = __(sleeper.IsAlive() ? sleeperCC : sleeperDeadCC);
                                if (drawArrows) player.SendConsoleCommand("ddraw.arrow", invokeTime + flickerDelay, color, sleeper.transform.position + new Vector3(0f, sleeper.transform.position.y + 10), sleeper.transform.position, 1);
                                if (drawText) player.SendConsoleCommand("ddraw.text", invokeTime + flickerDelay, color, sleeper.transform.position, string.Format("{0} <color={1}>{2}</color> <color={3}>{4}</color>{5}", sleeper.displayName, healthCC, Math.Floor(sleeper.health), distCC, currDistance, vanished));
                                if (drawX) player.SendConsoleCommand("ddraw.text", invokeTime + flickerDelay, color, sleeper.transform.position + new Vector3(0f, 1f, 0f), "X");
                                else if (drawBox) player.SendConsoleCommand("ddraw.box", invokeTime + flickerDelay, color, sleeper.transform.position, GetScale(currDistance));
                            }
                            else player.SendConsoleCommand("ddraw.box", invokeTime + flickerDelay, Color.cyan, sleeper.transform.position + new Vector3(0f, 1f, 0f), 5f);

                            if (objectsLimit > 0 && ++drawnObjects[player.userID] > objectsLimit) return;
                        }
                    }

                    if (!LatencyAccepted(tick))
                        return;

                    error = "DEAD";
                    if (showDead || showAll)
                    {
                        foreach (var corpse in cache.Corpses)
                        {
                            if (corpse.Key == null)
                                continue;

                            double currDistance = Math.Floor(Vector3.Distance(source.transform.position, corpse.Key.transform.position));

                            if (currDistance < corpseDistance && currDistance < maxDistance)
                            {
                                if (drawText) player.SendConsoleCommand("ddraw.text", invokeTime + flickerDelay, __(corpseCC), corpse.Key.transform.position + new Vector3(0f, 0.25f, 0f), string.Format("{0} ({1})", corpse.Value.Name, corpse.Value.Info));
                                if (drawBox) player.SendConsoleCommand("ddraw.box", invokeTime + flickerDelay, __(corpseCC), corpse.Key, GetScale(currDistance));
                                if (objectsLimit > 0 && ++drawnObjects[player.userID] > objectsLimit) return;
                            }
                        }
                    }

                    if (!LatencyAccepted(tick))
                        return;

                    if (showNPC || showAll)
                    {
                        error = "ZOMBIECACHE";
                        foreach(var zombie in cache.Zombies.ToList())
                        {
                            if (zombie == null || zombie.transform == null || zombie.net == null)
                            {
                                cache.Zombies.Remove(zombie);
                                continue;
                            }

                            double currDistance = Math.Floor(Vector3.Distance(zombie.transform.position, source.transform.position));

                            if (currDistance > maxDistance)
                                continue;

                            if (currDistance < playerDistance)
                            {
                                if (drawArrows) player.SendConsoleCommand("ddraw.arrow", invokeTime + flickerDelay, __(zombieCC), zombie.transform.position + new Vector3(0f, zombie.transform.position.y + 10), zombie.transform.position, 1);
                                if (drawText) player.SendConsoleCommand("ddraw.text", invokeTime + flickerDelay, __(zombieCC), zombie.transform.position + new Vector3(0f, 2f, 0f), string.Format("{0} <color={1}>{2}</color> <color={3}>{4}</color>", ins.msg("Zombie", player.UserIDString), healthCC, Math.Floor(zombie.health), distCC, currDistance));
                                if (drawBox) player.SendConsoleCommand("ddraw.box", invokeTime + flickerDelay, __(zombieCC), zombie.transform.position + new Vector3(0f, 1f, 0f), GetScale(currDistance));
                            }
                            else player.SendConsoleCommand("ddraw.box", invokeTime + flickerDelay, __(zombieCC), zombie.transform.position + new Vector3(0f, 1f, 0f), 5f);

                            if (objectsLimit > 0 && ++drawnObjects[player.userID] > objectsLimit) return;
                        }

                        error = "NPCCACHE";
                        foreach (var target in cache.NPC.ToList())
                        {
                            if (target?.transform == null)
                            {
                                cache.NPC.Remove(target);
                                continue;
                            }

                            double currDistance = Math.Floor(Vector3.Distance(target.transform.position, source.transform.position));

                            if (player == target || currDistance > maxDistance)
                                continue;

                            var color = target.ShortPrefabName == "scientist" ? Color.yellow : target.ShortPrefabName == "murderer" ? Color.black : Color.blue;
                            string npcColor = color == Color.yellow ? scientistCC : color == Color.black ? murdererCC : npcCC;

                            if (currDistance < playerDistance)
                            {
                                string displayName = target.displayName ?? (target.ShortPrefabName == "scientist" ? ins.msg("scientist", player.UserIDString) : ins.msg("npc", player.UserIDString));
                                if (drawArrows) player.SendConsoleCommand("ddraw.arrow", invokeTime + flickerDelay, __(npcColor), target.transform.position + new Vector3(0f, target.transform.position.y + 10), target.transform.position, 1);
                                if (drawText) player.SendConsoleCommand("ddraw.text", invokeTime + flickerDelay, __(npcColor), target.transform.position + new Vector3(0f, 2f, 0f), string.Format("{0} <color={1}>{2}</color> <color={3}>{4}</color>", displayName, healthCC, Math.Floor(target.health), distCC, currDistance));
                                if (drawBox) player.SendConsoleCommand("ddraw.box", invokeTime + flickerDelay, __(npcColor), target.transform.position + new Vector3(0f, 1f, 0f), target.GetHeight(target.modelState.ducked));
                            }
                            else player.SendConsoleCommand("ddraw.box", invokeTime + flickerDelay, __(npcColor), target.transform.position + new Vector3(0f, 1f, 0f), 5f);

                            if (objectsLimit > 0 && ++drawnObjects[player.userID] > objectsLimit) return;
                        }

                        error = "ANIMALS";
                        foreach (var npc in BaseNetworkable.serverEntities.Where(e => e is BaseNpc).Cast<BaseNpc>().ToList())
                        {
                            if (npc.ShortPrefabName == "zombie")
                                continue;

                            double currDistance = Math.Floor(Vector3.Distance(npc.transform.position, source.transform.position));

                            if (currDistance < npcDistance && currDistance < maxDistance)
                            {
                                if (drawText) player.SendConsoleCommand("ddraw.text", invokeTime + flickerDelay, __(npcCC), npc.transform.position + new Vector3(0f, 1f, 0f), string.Format("{0} <color={1}>{2}</color> <color={3}>{4}</color>", npc.ShortPrefabName, healthCC, Math.Floor(npc.health), distCC, currDistance));
                                if (drawBox) player.SendConsoleCommand("ddraw.box", invokeTime + flickerDelay, __(npcCC), npc.transform.position + new Vector3(0f, 1f, 0f), npc.bounds.size.y);
                                if (objectsLimit > 0 && ++drawnObjects[player.userID] > objectsLimit) return;
                            }
                        }
                    }

                    if (!LatencyAccepted(tick))
                        return;

                    error = "ORE";
                    if (showOre || showAll)
                    {
                        foreach (var ore in cache.Ores)
                        {
                            double currDistance = Math.Floor(Vector3.Distance(source.transform.position, ore.Key));

                            if (currDistance < oreDistance && currDistance < maxDistance)
                            {
                                object value = showResourceAmounts ? string.Format("({0})", ore.Value.Info) : string.Format("<color={0}>{1}</color>", distCC, currDistance);
                                if (drawText) player.SendConsoleCommand("ddraw.text", invokeTime + flickerDelay, __(resourceCC), ore.Key + new Vector3(0f, 1f, 0f), string.Format("{0} {1}", ore.Value.Name, value));
                                if (drawBox) player.SendConsoleCommand("ddraw.box", invokeTime + flickerDelay, __(resourceCC), ore.Key + new Vector3(0f, 1f, 0f), GetScale(currDistance));
                                if (objectsLimit > 0 && ++drawnObjects[player.userID] > objectsLimit) return;
                            }
                        }
                    }

                    if (!LatencyAccepted(tick))
                        return;

                    error = "COLLECTABLES";
                    if (showCollectible || showAll)
                    {
                        foreach (var col in cache.Collectibles)
                        {
                            var currDistance = Math.Floor(Vector3.Distance(col.Key, source.transform.position));

                            if (currDistance < colDistance && currDistance < maxDistance)
                            {
                                object value = showResourceAmounts ? string.Format("({0})", col.Value.Info) : string.Format("<color={0}>{1}</color>", distCC, currDistance);
                                if (drawText) player.SendConsoleCommand("ddraw.text", invokeTime + flickerDelay, __(colCC), col.Key + new Vector3(0f, 1f, 0f), string.Format("{0} {1}", col.Value.Name, value));
                                if (drawBox) player.SendConsoleCommand("ddraw.box", invokeTime + flickerDelay, __(colCC), col.Key + new Vector3(0f, 1f, 0f), col.Value.Size);
                                if (objectsLimit > 0 && ++drawnObjects[player.userID] > objectsLimit) return;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    ins.Puts("Error @{0}: {1} --- {2}", error, ex.Message, ex.StackTrace);
                    player.ChatMessage(ins.msg("Exception", player.UserIDString));
                }
                finally
                {
                    if (!LatencyAccepted(tick))
                    {
                        var ms = (DateTime.Now - tick).TotalMilliseconds;
                        string message = ins.msg("DoESP", player.UserIDString, ms, latencyMs);
                        ins.Puts("{0} for {1} ({2})", message, player.displayName, player.UserIDString);
                        GameObject.Destroy(this);
                    }
                }
            }
        }
        
        static void DrawVision(BasePlayer player, BasePlayer target, float invokeTime)
        {
            RaycastHit hit;

            if (!Physics.Raycast(target.eyes.HeadRay(), out hit, Mathf.Infinity))
                return;

            player.SendConsoleCommand("ddraw.arrow", invokeTime + flickerDelay, Color.red, target.eyes.position + new Vector3(0f, 0.115f, 0f), hit.point, 0.15f);
        }
        
        static Color __(string text)
        {
            Color color;

            if (!ColorUtility.TryParseHtmlString(text, out color))
                color = Color.white;
            
            return color;
        }

        static string _(string s)
        {
            foreach (string str in tags)
                s = s.Replace(str, "");

            return s;
        }

        void Track(BasePlayer player)
        {
            if (!player.gameObject.GetComponent<PlayerTracker>())
                player.gameObject.AddComponent<PlayerTracker>();

            if (trackerTimers.ContainsKey(player.userID))
            {
                trackerTimers[player.userID]?.Destroy();
                trackerTimers.Remove(player.userID);
            }
        }

        static void RemoveFromCache(BaseNetworkable entity)
        {
            if (!init || entity == null)
                return;

            if (entity.transform != null)
            {
                if (cache.Backpacks.ContainsKey(entity.transform.position))
                    cache.Backpacks.Remove(entity.transform.position);
                else if (cache.Ores.ContainsKey(entity.transform.position))
                    cache.Ores.Remove(entity.transform.position);
                else if (cache.Containers.ContainsKey(entity.transform.position))
                    cache.Containers.Remove(entity.transform.position);
                else if (cache.Bags.ContainsKey(entity.transform.position))
                    cache.Bags.Remove(entity.transform.position);
                else if (cache.TC.ContainsKey(entity.transform.position))
                    cache.TC.Remove(entity.transform.position);
                else if (cache.Turrets.ContainsKey(entity.transform.position))
                    cache.Turrets.Remove(entity.transform.position);
                else if (cache.Collectibles.ContainsKey(entity.transform.position))
                    cache.Collectibles.Remove(entity.transform.position);
            }

            if (entity is BasePlayer && cache.NPC.Contains(entity as BasePlayer))
                cache.NPC.Remove(entity as BasePlayer);
            else if (entity is BradleyAPC && trackBradleys)
                cache.Bradley.Remove(entity as BradleyAPC);
            else if (entity is BaseHelicopter && trackHelis)
                cache.Helis.Remove(entity as BaseHelicopter);
            else if (entity is PlayerCorpse)
                cache.Corpses.Remove(entity as PlayerCorpse);
            else if (entity is SupplyDrop)
                cache.Airdrop.Remove(entity as SupplyDrop);
        }

        static bool AddToCache(BaseNetworkable entity)
        {
            if (!init || entity == null || entity.IsDestroyed)
                return false;

            if (entity is BasePlayer)
            {
                var player = entity as BasePlayer;

                if (!player.userID.IsSteamId() && !cache.NPC.Contains(player))
                {
                    if (entity is NPCPlayer || entity.ShortPrefabName == "scientist" || entity.ShortPrefabName == "murderer")
                    {
                        cache.NPC.Add(player);
                        return true;
                    }
                }

                return false;
            }
            else if (entity is Zombie)
            {
                var zombie = entity as Zombie;

                if (!cache.Zombies.Contains(zombie))
                {
                    cache.Zombies.Add(zombie);
                    return true;
                }
            }
            else if (entity is BaseHelicopter && trackHelis)
            {
                var heli = entity as BaseHelicopter;

                if (!cache.Helis.Contains(heli))
                {
                    cache.Helis.Add(heli);
                    return true;
                }
            }
            else if (entity is BradleyAPC && trackBradleys)
            {
                var apc = entity as BradleyAPC;

                if (!cache.Bradley.Contains(apc))
                {
                    cache.Bradley.Add(apc);
                    return true;
                }
            }
            else if (entity is BuildingPrivlidge && entity.transform != null)
            {
                if (!cache.TC.ContainsKey(entity.transform.position))
                {
                    cache.TC.Add(entity.transform.position, new CachedInfo() { Size = 3f });
                    return true;
                }
            }
            else if (entity is SupplyDrop)
            {
                var drop = entity as SupplyDrop;

                if (!cache.Airdrop.Contains(drop))
                {
                    cache.Airdrop.Add(drop);
                    return true;
                }
            }
            else if (entity is StorageContainer && entity.transform != null)
            {
                if (cache.Containers.ContainsKey(entity.transform.position))
                    return false;

                if (entity.name.Contains("turret"))
                {
                    if (!cache.Turrets.ContainsKey(entity.transform.position))
                    {
                        cache.Turrets.Add(entity.transform.position, new CachedInfo() { Size = 1f, Info = entity.GetComponent<StorageContainer>()?.inventory?.itemList?.Select(item => item.amount).Sum() ?? 0 });
                        return true;
                    }
                }
                else if (entity.name.Contains("box") || entity.ShortPrefabName.Equals("heli_crate") || entity.name.Contains("loot") || entity.name.Contains("crate_") || entity.name.Contains("stash"))
                {
                    cache.Containers.Add(entity.transform.position, new CachedInfo() { Name = entity.ShortPrefabName, Info = entity.net.ID });
                    return true;
                }
            }
            else if (entity is DroppedItemContainer && entity.transform != null)
            {
                var position = entity.transform.position;

                while (cache.Backpacks.ContainsKey(position))
                {
                    position.y += 1f;
                }

                cache.Backpacks.Add(position, new CachedInfo() { Name = entity.ShortPrefabName, Info = entity.net.ID });
                return true;
            }
            else if (entity is SleepingBag && entity.transform != null)
            {
                if (!cache.Bags.ContainsKey(entity.transform.position))
                {
                    cache.Bags.Add(entity.transform.position, new CachedInfo() { Size = 0.5f });
                    return true;
                }
            }
            else if (entity is PlayerCorpse)
            {
                var corpse = entity as PlayerCorpse;

                if (!cache.Corpses.ContainsKey(corpse))
                {
                    int amount = 0;

                    if (corpse.containers != null)
                        foreach (var container in corpse.containers)
                            amount += container.itemList.Count;

                    cache.Corpses.Add(corpse, new CachedInfo() { Name = corpse.parentEnt?.ToString() ?? corpse.playerSteamID.ToString(), Info = amount });
                    return true;
                }
            }
            else if (entity is CollectibleEntity && entity.transform != null)
            {
                if (!cache.Collectibles.ContainsKey(entity.transform.position))
                {
                    cache.Collectibles.Add(entity.transform.position, new CachedInfo() { Name = _(entity.ShortPrefabName), Size = 0.5f, Info = Math.Ceiling(entity.GetComponent<CollectibleEntity>()?.itemList?.Select(item => item.amount).Sum() ?? 0) });
                    return true;
                }
            }
            else if (entity.name.Contains("-ore") && entity.transform != null)
            {
                if (!cache.Ores.ContainsKey(entity.transform.position))
                {
                    cache.Ores.Add(entity.transform.position, new CachedInfo() { Name = _(entity.ShortPrefabName), Info = Math.Ceiling(entity.GetComponentInParent<ResourceDispenser>()?.containedItems?.Select(item => item.amount).Sum() ?? 0) });
                    return true;
                }
            }

            return false;
        }

        static float GetScale(double v) => v <= 50 ? 1f : v > 50 && v <= 100 ? 2f : v > 100 && v <= 150 ? 2.5f : v > 150 && v <= 200 ? 3f : v > 200 && v <= 300 ? 4f : 5f;

        [ConsoleCommand("espgui")]
        void ccmdESPGUI(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();

            if (!player || !arg.HasArgs())
                return;

            cmdESP(player, "espgui", arg.Args);
        }

        bool HasAccess(BasePlayer player)
        {
            if (player.IsDeveloper)
                return true;

            if (authorized.Count > 0)
                return authorized.Contains(player.UserIDString);

            if (player.net.connection.authLevel >= authLevel)
                return true;

            if (permission.UserHasPermission(player.UserIDString, "fauxadmin.allowed") && permission.UserHasPermission(player.UserIDString, permName) && player.HasPlayerFlag(BasePlayer.PlayerFlags.IsAdmin))
                return true;

            return false;
        }

        void cmdESP(BasePlayer player, string command, string[] args)
        {
            if (!HasAccess(player))
            {
                player.ChatMessage(msg("NotAllowed", player.UserIDString));
                return;
            }

            if (args.Length == 1)
            {
                switch (args[0].ToLower())
                {
                    case "drops":
                        {
                            int drops = 0;

                            foreach (var entity in BaseNetworkable.serverEntities.Where(e => e is DroppedItem || e is Landmine || e is BearTrap))
                            {
                                var drop = entity as DroppedItem;
                                string shortname = drop?.item?.info.shortname ?? entity.ShortPrefabName;
                                double currDistance = Math.Floor(Vector3.Distance(entity.transform.position, player.transform.position));
                                
                                if (currDistance < lootDistance)
                                {
                                    if (drawText) player.SendConsoleCommand("ddraw.text", 30f, Color.red, entity.transform.position, string.Format("{0} <color=yellow>{1}</color>", shortname, currDistance));
                                    if (drawBox) player.SendConsoleCommand("ddraw.box", 30f, Color.red, entity.transform.position, 0.25f);
                                    drops++;
                                }
                            }

                            if (drops == 0)
                            {
                                player.ChatMessage(msg("NoDrops", player.UserIDString, lootDistance));
                            }
                        }
                        return;
                    case "online":
                        {
                            if (storedData.OnlineBoxes.Contains(player.UserIDString))
                                storedData.OnlineBoxes.Remove(player.UserIDString);
                            else
                                storedData.OnlineBoxes.Add(player.UserIDString);

                            player.ChatMessage(msg(storedData.OnlineBoxes.Contains(player.UserIDString) ? "BoxesOnlineOnly" : "BoxesAll", player.UserIDString));
                        }
                        return;
                    case "vision":
                        {
                            if (storedData.Visions.Contains(player.UserIDString))
                                storedData.Visions.Remove(player.UserIDString);
                            else
                                storedData.Visions.Add(player.UserIDString);

                            player.ChatMessage(msg(storedData.Visions.Contains(player.UserIDString) ? "VisionOn" : "VisionOff", player.UserIDString));
                        }
                        return;
                    case "ext":
                    case "extend":
                    case "extended":
                        {
                            if (storedData.Extended.Contains(player.UserIDString))
                                storedData.Extended.Remove(player.UserIDString);
                            else
                                storedData.Extended.Add(player.UserIDString);

                            player.ChatMessage(msg(storedData.Extended.Contains(player.UserIDString) ? "ExtendedPlayersOn" : "ExtendedPlayersOff", player.UserIDString));
                        }
                        return;
                }
            }

            if (!storedData.Filters.ContainsKey(player.UserIDString))
                storedData.Filters.Add(player.UserIDString, args.ToList());

            if (args.Length == 0 && player.GetComponent<ESP>())
            {
                GameObject.Destroy(player.GetComponent<ESP>());
                return;
            }

            args = args.Select(arg => arg.ToLower()).ToArray();

            if (args.Length == 1)
            {
                if (args[0] == "tracker")
                {
                    if (!usePlayerTracker)
                    {
                        player.ChatMessage(msg("TrackerDisabled", player.UserIDString));
                        return;
                    }

                    if (trackers.Count == 0)
                    {
                        player.ChatMessage(msg("NoTrackers", player.UserIDString));
                        return;
                    }

                    var lastPos = Vector3.zero;
                    bool inRange = false;
                    var colors = new List<Color>();

                    foreach (var kvp in trackers)
                    {
                        lastPos = Vector3.zero;

                        if (trackers[kvp.Key].Count > 0)
                        {
                            if (colors.Count == 0)
                                colors = new List<Color>() { Color.blue, Color.cyan, Color.gray, Color.green, Color.magenta, Color.red, Color.yellow };

                            var color = playersColor.ContainsKey(kvp.Key) ? playersColor[kvp.Key] : colors[UnityEngine.Random.Range(0, colors.Count - 1)];

                            playersColor[kvp.Key] = color;

                            if (colors.Contains(color))
                                colors.Remove(color);

                            foreach (var entry in trackers[kvp.Key])
                            {
                                if (Vector3.Distance(entry.Value, player.transform.position) < maxTrackReportDistance)
                                {
                                    if (lastPos == Vector3.zero)
                                    {
                                        lastPos = entry.Value;
                                        continue;
                                    }

                                    if (Vector3.Distance(lastPos, entry.Value) < overlapDistance) // this prevents arrow lines from being tangled upon other arrow lines into a giant clusterfuck
                                        continue;

                                    player.SendConsoleCommand("ddraw.arrow", trackDrawTime, color, lastPos, entry.Value, 0.1f);
                                    lastPos = entry.Value;
                                    inRange = true;
                                }
                            }

                            if (lastPos != Vector3.zero)
                            {
                                string name = covalence.Players.FindPlayerById(kvp.Key.ToString()).Name;
                                player.SendConsoleCommand("ddraw.text", trackDrawTime, color, lastPos, string.Format("{0} ({1})", name, trackers[kvp.Key].Count));
                            }
                        }
                    }

                    if (!inRange)
                        player.ChatMessage(msg("NoTrackersInRange", player.UserIDString, maxTrackReportDistance));

                    return;
                }
                else if (args[0] == "help")
                {
                    player.ChatMessage(msg("Help1", player.UserIDString, "all, bag, box, col, dead, loot, npc, ore, stash, tc, turret"));
                    player.ChatMessage(msg("Help2", player.UserIDString, szChatCommand, "online"));
                    player.ChatMessage(msg("Help3", player.UserIDString, szChatCommand, "ui"));
                    player.ChatMessage(msg("Help4", player.UserIDString, szChatCommand, "tracker"));
                    player.ChatMessage(msg("Help7", player.UserIDString, szChatCommand, "vision"));
                    player.ChatMessage(msg("Help8", player.UserIDString, szChatCommand, "ext"));
                    player.ChatMessage(msg("Help9", player.UserIDString, szChatCommand, lootDistance));
                    player.ChatMessage(msg("Help5", player.UserIDString, szChatCommand));
                    player.ChatMessage(msg("Help6", player.UserIDString, szChatCommand));
                    player.ChatMessage(msg("PreviousFilter", player.UserIDString, command));
                    return;
                }
                else if (args[0].Contains("ui"))
                {
                    if (storedData.Filters[player.UserIDString].Contains(args[0]))
                        storedData.Filters[player.UserIDString].Remove(args[0]);

                    if (storedData.Hidden.Contains(player.UserIDString))
                    {
                        storedData.Hidden.Remove(player.UserIDString);
                        player.ChatMessage(msg("GUIShown", player.UserIDString));
                    }
                    else
                    {
                        storedData.Hidden.Add(player.UserIDString);
                        player.ChatMessage(msg("GUIHidden", player.UserIDString));
                    }

                    args = storedData.Filters[player.UserIDString].ToArray();
                }
                else if (args[0] == "list")
                {
                    player.ChatMessage(activeRadars.Count == 0 ? msg("NoActiveRadars", player.UserIDString) : msg("ActiveRadars", player.UserIDString, string.Join(", ", activeRadars.Select(radar => radar.player.displayName).ToArray())));
                    return;
                }
                else if (args[0] == "f")
                    args = storedData.Filters[player.UserIDString].ToArray();
            }

            if (command == "espgui")
            {
                string filter = storedData.Filters[player.UserIDString].Find(f => f.Contains(args[0]) || args[0].Contains(f)) ?? args[0];

                if (storedData.Filters[player.UserIDString].Contains(filter))
                    storedData.Filters[player.UserIDString].Remove(filter);
                else
                    storedData.Filters[player.UserIDString].Add(filter);

                args = storedData.Filters[player.UserIDString].ToArray();
            }
            else
                storedData.Filters[player.UserIDString] = args.ToList();

            var esp = player.GetComponent<ESP>() ?? player.gameObject.AddComponent<ESP>();
            float invokeTime, maxDistance, outTime, outDistance;

            if (args.Length > 0 && float.TryParse(args[0], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out outTime))
                invokeTime = outTime < 0.1f ? 0.1f : outTime;
            else
                invokeTime = defaultInvokeTime;

            if (args.Length > 1 && float.TryParse(args[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out outDistance))
                maxDistance = outDistance <= 0f ? defaultMaxDistance : outDistance;
            else
                maxDistance = defaultMaxDistance;

            esp.showAll = args.Any(arg => arg.Contains("all"));
            esp.showBags = args.Any(arg => arg.Contains("bag"));
            esp.showBox = args.Any(arg => arg.Contains("box"));
            esp.showCollectible = args.Any(arg => arg.Contains("col"));
            esp.showDead = args.Any(arg => arg.Contains("dead"));
            esp.showLoot = args.Any(arg => arg.Contains("loot"));
            esp.showNPC = args.Any(arg => arg.Contains("npc"));
            esp.showOre = args.Any(arg => arg.Contains("ore"));
            esp.showSleepers = args.Any(arg => arg.Contains("sleep"));
            esp.showStash = args.Any(arg => arg.Contains("stash"));
            esp.showTC = args.Any(arg => arg.Contains("tc"));
            esp.showTurrets = args.Any(arg => arg.Contains("turret"));

            if (showUI)
            {
                string gui;
                if (guiInfo.TryGetValue(player.UserIDString, out gui))
                {
                    CuiHelper.DestroyUi(player, gui);
                    guiInfo.Remove(player.UserIDString);
                }

                if (!storedData.Hidden.Contains(player.UserIDString))
                {
                    string espUI = uiJson;

                    espUI = espUI.Replace("{anchorMin}", anchorMin);
                    espUI = espUI.Replace("{anchorMax}", anchorMax);
                    espUI = espUI.Replace("{colorAll}", esp.showAll ? "255 0 0 1" : "1 1 1 1");
                    espUI = espUI.Replace("{colorBags}", esp.showBags ? "255 0 0 1" : "1 1 1 1");
                    espUI = espUI.Replace("{colorBox}", esp.showBox ? "255 0 0 1" : "1 1 1 1");
                    espUI = espUI.Replace("{colorCol}", esp.showCollectible ? "255 0 0 1" : "1 1 1 1");
                    espUI = espUI.Replace("{colorDead}", esp.showDead ? "255 0 0 1" : "1 1 1 1");
                    espUI = espUI.Replace("{colorLoot}", esp.showLoot ? "255 0 0 1" : "1 1 1 1");
                    espUI = espUI.Replace("{colorNPC}", esp.showNPC ? "255 0 0 1" : "1 1 1 1");
                    espUI = espUI.Replace("{colorOre}", esp.showOre ? "255 0 0 1" : "1 1 1 1");
                    espUI = espUI.Replace("{colorSleepers}", esp.showSleepers ? "255 0 0 1" : "1 1 1 1");
                    espUI = espUI.Replace("{colorStash}", esp.showStash ? "255 0 0 1" : "1 1 1 1");
                    espUI = espUI.Replace("{colorTC}", esp.showTC ? "255 0 0 1" : "1 1 1 1");
                    espUI = espUI.Replace("{colorTurrets}", esp.showTurrets ? "255 0 0 1" : "1 1 1 1");

                    guiInfo[player.UserIDString] = CuiHelper.GetGuid();
                    CuiHelper.AddUi(player, espUI.Replace("{guid}", guiInfo[player.UserIDString]));
                }
            }

            esp.invokeTime = invokeTime;
            esp.maxDistance = maxDistance;

            esp.CancelInvoke("DoESP");
            esp.Invoke("DoESP", invokeTime);
            esp.InvokeRepeating("DoESP", 0f, invokeTime);

            if (!IsRadar(player.UserIDString))
                activeRadars.Add(esp);

            if (command == "espgui")
                return;

            player.ChatMessage(msg("Activated", player.UserIDString, invokeTime, maxDistance, command));
        }

        #region Config
        bool Changed;
        static bool drawText = true;
        static bool drawBox = false;
        static bool drawArrows = false;
        static bool drawX;
        static int authLevel;
        static float defaultInvokeTime;
        static float defaultMaxDistance;

        static float adDistance;
        static float boxDistance;
        static float playerDistance;
        static float tcDistance;
        static float stashDistance;
        static float corpseDistance;
        static float oreDistance;
        static float lootDistance;
        static float colDistance;
        static float bagDistance;
        static float npcDistance;
        static float turretDistance;
        static float latencyMs;
        static int objectsLimit;
        static bool showLootContents;
        static bool showAirdropContents;
        static bool showStashContents;
        static bool drawEmptyContainers;
        static bool showResourceAmounts;
        static bool trackHelis;
        static bool trackBradleys;
        static bool showHeliRotorHealth;
        static bool usePlayerTracker;
        static bool trackAdmins;
        static float trackerUpdateInterval;
        static float trackerAge;
        static float maxTrackReportDistance;
        static float trackDrawTime;
        static float overlapDistance;
        static int backpackContentAmount;
        static int groupLimit;
        static float groupRange;
        static float groupCountHeight;
        static float inactiveTimeLimit;
        static int deactiveTimeLimit;
        static bool showUI;
        static bool useBypass;

        static string distCC;
        static string heliCC;
        static string bradleyCC;
        static string activeCC;
        static string activeDeadCC;
        static string corpseCC;
        static string sleeperCC;
        static string sleeperDeadCC;
        static string healthCC;
        static string backpackCC;
        static string zombieCC;
        static string scientistCC;
        static string murdererCC;
        static string npcCC;
        static string resourceCC;
        static string colCC;
        static string tcCC;
        static string bagCC;
        static string airdropCC;
        static string atCC;
        static string boxCC;
        static string lootCC;
        static string stashCC;
        static string groupColorDead;
        static string groupColorBasic;

        static string szChatCommand;
        static List<object> authorized;
        static List<string> itemExceptions = new List<string>();
        string anchorMin;
        string anchorMax;
        //static string voiceSymbol;
        static bool useVoiceDetection;
        static int voiceInterval;
        static float voiceDistance;

        List<object> ItemExceptions
        {
            get
            {
                return new List<object> { "bottle", "planner", "rock", "torch", "can.", "arrow." };
            }
        }

        private static bool useGroupColors;
        private static Dictionary<int, string> groupColors = new Dictionary<int, string>();

        private static string GetGroupColor(int index)
        {
            if (useGroupColors && groupColors.ContainsKey(index))
                return groupColors[index];

            return groupColorBasic;
        }

        private void SetupGroupColors(List<object> list)
        {
            groupColors.Clear();

            if (list != null && list.Count > 0)
            {
                foreach (var entry in list)
                {
                    if (entry is Dictionary<string, object>)
                    {
                        var dict = (Dictionary<string, object>)entry;

                        foreach(var kvp in dict)
                        {
                            int key = 0;
                            if (int.TryParse(kvp.Key.ToString(), out key))
                            {
                                string value = kvp.Value.ToString();

                                if (value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                                {
                                    value = "#" + value;
                                }

                                groupColors[key] = value;
                            }
                        }
                    }
                }
            }
        }

        private List<object> DefaultGroupColors
        {
            get
            {
                return new List<object>
                {
                    new Dictionary<string, object>()
                    {
                        ["0"] = "red",
                        ["1"] = "green",
                        ["2"] = "blue",
                        ["3"] = "orange",
                        ["4"] = "yellow",
                    },
                };
            }
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["NotAllowed"] = "You are not allowed to use this command.",
                ["PreviousFilter"] = "To use your previous filter type <color=orange>/{0} f</color>",
                ["Activated"] = "ESP Activated - {0}s refresh - {1}m distance. Use <color=orange>/{2} help</color> for help.",
                ["Deactivated"] = "ESP Deactivated.",
                ["DoESP"] = "DoESP() took {0}ms (max: {1}ms) to execute!",
                ["TrackerDisabled"] = "Player Tracker is disabled.",
                ["NoTrackers"] = "No players have been tracked yet.",
                ["NoTrackersInRange"] = "No trackers in range ({0}m)",
                ["Exception"] = "ESP Tool: An error occured. Please check the server console.",
                ["GUIShown"] = "GUI will be shown",
                ["GUIHidden"] = "GUI will now be hidden",
                ["InvalidID"] = "{0} is not a valid steam id. Entry removed.",
                ["BoxesAll"] = "Now showing all boxes.",
                ["BoxesOnlineOnly"] = "Now showing online player boxes only.",
                ["Help1"] = "<color=orange>Available Filters</color>: {0}",
                ["Help2"] = "<color=orange>/{0} {1}</color> - Toggles showing online players boxes only when using the <color=red>box</color> filter.",
                ["Help3"] = "<color=orange>/{0} {1}</color> - Toggles quick toggle UI on/off",
                ["Help4"] = "<color=orange>/{0} {1}</color> - Draw on your screen the movement of nearby players. Must be enabled.",
                ["Help5"] = "e.g: <color=orange>/{0} 1 1000 box loot stash</color>",
                ["Help6"] = "e.g: <color=orange>/{0} 0.5 400 all</color>",
                ["VisionOn"] = "You will now see where players are looking.",
                ["VisionOff"] = "You will no longer see where players are looking.",
                ["ExtendedPlayersOn"] = "Extended information for players is now on.",
                ["ExtendedPlayersOff"] = "Extended information for players is now off.",
                ["Help7"] = "<color=orange>/{0} {1}</color> - Toggles showing where players are looking.",
                ["Help8"] = "<color=orange>/{0} {1}</color> - Toggles extended information for players.",
                ["backpack"] = "backpack",
                ["scientist"] = "scientist",
                ["npc"] = "npc",
                ["NoDrops"] = "No item drops found within {0}m",
                ["Help9"] = "<color=orange>/{0} drops</color> - Show all dropped items within {1}m.",
                ["Zombie"] = "<color=red>Zombie</color>",
                ["NoActiveRadars"] = "No one is using Radar at the moment.",
                ["ActiveRadars"] = "Active radar users: {0}",
            }, this);
        }

        void LoadVariables()
        {
            authorized = GetConfig("Settings", "Restrict Access To Steam64 IDs", new List<object>()) as List<object>;

            foreach (var auth in authorized.ToList())
            {
                if (auth == null || !auth.ToString().IsSteamId())
                {
                    PrintWarning(msg("InvalidID", null, auth == null ? "null" : auth.ToString()));
                    authorized.Remove(auth);
                }
            }

            authLevel = authorized.Count == 0 ? Convert.ToInt32(GetConfig("Settings", "Restrict Access To Auth Level", 1)) : int.MaxValue;
            defaultMaxDistance = Convert.ToSingle(GetConfig("Settings", "Default Distance", 500.0));
            defaultInvokeTime = Convert.ToSingle(GetConfig("Settings", "Default Refresh Time", 5.0));
            latencyMs = Convert.ToInt32(GetConfig("Settings", "Latency Cap In Milliseconds (0 = no cap)", 1000.0));
            objectsLimit = Convert.ToInt32(GetConfig("Settings", "Objects Drawn Limit (0 = unlimited)", 250));
            itemExceptions = (GetConfig("Settings", "Dropped Item Exceptions", ItemExceptions) as List<object>).Cast<string>().ToList();
            inactiveTimeLimit = Convert.ToSingle(GetConfig("Settings", "Deactivate Radar After X Seconds Inactive", 300f));
            deactiveTimeLimit = Convert.ToInt32(GetConfig("Settings", "Deactivate Radar After X Minutes", 0));
            showUI = Convert.ToBoolean(GetConfig("Settings", "User Interface Enabled", true));
            useBypass = Convert.ToBoolean(GetConfig("Settings", "Use Bypass Permission", false));

            showLootContents = Convert.ToBoolean(GetConfig("Options", "Show Barrel And Crate Contents", false));
            showAirdropContents = Convert.ToBoolean(GetConfig("Options", "Show Airdrop Contents", false));
            showStashContents = Convert.ToBoolean(GetConfig("Options", "Show Stash Contents", false));
            drawEmptyContainers = Convert.ToBoolean(GetConfig("Options", "Draw Empty Containers", true));
            showResourceAmounts = Convert.ToBoolean(GetConfig("Options", "Show Resource Amounts", true));
            backpackContentAmount = Convert.ToInt32(GetConfig("Options", "Show X Items In Backpacks [0 = amount only]", 3));

            drawArrows = Convert.ToBoolean(GetConfig("Drawing Methods", "Draw Arrows On Players", false));
            drawBox = Convert.ToBoolean(GetConfig("Drawing Methods", "Draw Boxes", false));
            drawText = Convert.ToBoolean(GetConfig("Drawing Methods", "Draw Text", true));

            drawX = Convert.ToBoolean(GetConfig("Group Limit", "Draw Distant Players With X", true));
            groupLimit = Convert.ToInt32(GetConfig("Group Limit", "Limit", 4));
            groupRange = Convert.ToSingle(GetConfig("Group Limit", "Range", 50f));
            groupCountHeight = Convert.ToSingle(GetConfig("Group Limit", "Height Offset [0.0 = disabled]", 0f));

            adDistance = Convert.ToSingle(GetConfig("Drawing Distances", "Airdrop Crates", 400f));
            npcDistance = Convert.ToSingle(GetConfig("Drawing Distances", "Animals", 200));
            bagDistance = Convert.ToSingle(GetConfig("Drawing Distances", "Sleeping Bags", 250));
            boxDistance = Convert.ToSingle(GetConfig("Drawing Distances", "Boxes", 100));
            colDistance = Convert.ToSingle(GetConfig("Drawing Distances", "Collectibles", 100));
            corpseDistance = Convert.ToSingle(GetConfig("Drawing Distances", "Player Corpses", 200));
            playerDistance = Convert.ToSingle(GetConfig("Drawing Distances", "Players", 500));
            lootDistance = Convert.ToSingle(GetConfig("Drawing Distances", "Loot Containers", 150));
            oreDistance = Convert.ToSingle(GetConfig("Drawing Distances", "Resources (Ore)", 200));
            stashDistance = Convert.ToSingle(GetConfig("Drawing Distances", "Stashes", 250));
            tcDistance = Convert.ToSingle(GetConfig("Drawing Distances", "Tool Cupboards", 100));
            turretDistance = Convert.ToSingle(GetConfig("Drawing Distances", "Turrets", 100));

            trackBradleys = Convert.ToBoolean(GetConfig("Bradleys", "Track Bradley APC", true));

            trackHelis = Convert.ToBoolean(GetConfig("Helicopters", "Track Helicopters", true));
            showHeliRotorHealth = Convert.ToBoolean(GetConfig("Helicopters", "Show Rotors Health", false));

            usePlayerTracker = Convert.ToBoolean(GetConfig("Player Movement Tracker", "Enabled", false));
            trackAdmins = Convert.ToBoolean(GetConfig("Player Movement Tracker", "Track Admins", false));
            trackerUpdateInterval = Convert.ToSingle(GetConfig("Player Movement Tracker", "Update Tracker Every X Seconds", 1f));
            trackerAge = Convert.ToInt32(GetConfig("Player Movement Tracker", "Positions Expire After X Seconds", 600));
            maxTrackReportDistance = Convert.ToSingle(GetConfig("Player Movement Tracker", "Max Reporting Distance", 200f));
            trackDrawTime = Convert.ToSingle(GetConfig("Player Movement Tracker", "Draw Time", 60f));
            overlapDistance = Convert.ToSingle(GetConfig("Player Movement Tracker", "Overlap Reduction Distance", 5f));
            
            distCC = Convert.ToString(GetConfig("Color-Hex Codes", "Distance", "#ffa500"));
            heliCC = Convert.ToString(GetConfig("Color-Hex Codes", "Helicopters", "#ff00ff"));
            bradleyCC = Convert.ToString(GetConfig("Color-Hex Codes", "Bradley", "#ff00ff"));
            activeCC = Convert.ToString(GetConfig("Color-Hex Codes", "Online Player", "#ffffff"));
            activeDeadCC = Convert.ToString(GetConfig("Color-Hex Codes", "Online Dead Player", "#ff0000"));
            sleeperCC = Convert.ToString(GetConfig("Color-Hex Codes", "Sleeping Player", "#00ffff"));
            sleeperDeadCC = Convert.ToString(GetConfig("Color-Hex Codes", "Sleeping Dead Player", "#ff0000"));
            healthCC = Convert.ToString(GetConfig("Color-Hex Codes", "Health", "#ff0000"));
            backpackCC = Convert.ToString(GetConfig("Color-Hex Codes", "Backpacks", "#c0c0c0"));
            zombieCC = Convert.ToString(GetConfig("Color-Hex Codes", "Zombies", "#ff0000"));
            scientistCC = Convert.ToString(GetConfig("Color-Hex Codes", "Scientists", "#ffff00"));
            murdererCC = Convert.ToString(GetConfig("Color-Hex Codes", "Murderers", "#000000"));
            npcCC = Convert.ToString(GetConfig("Color-Hex Codes", "Animals", "#0000ff"));
            resourceCC = Convert.ToString(GetConfig("Color-Hex Codes", "Resources", "#ffff00"));
            colCC = Convert.ToString(GetConfig("Color-Hex Codes", "Collectibles", "#ffff00"));
            tcCC = Convert.ToString(GetConfig("Color-Hex Codes", "Tool Cupboards", "#000000"));
            bagCC = Convert.ToString(GetConfig("Color-Hex Codes", "Sleeping Bags", "#ff00ff"));
            airdropCC = Convert.ToString(GetConfig("Color-Hex Codes", "Airdrops", "#ff00ff"));
            atCC = Convert.ToString(GetConfig("Color-Hex Codes", "AutoTurrets", "#ffff00"));
            corpseCC = Convert.ToString(GetConfig("Color-Hex Codes", "Corpses", "#ffff00"));
            boxCC = Convert.ToString(GetConfig("Color-Hex Codes", "Box", "#ff00ff"));
            lootCC = Convert.ToString(GetConfig("Color-Hex Codes", "Loot", "#ffff00"));
            stashCC = Convert.ToString(GetConfig("Color-Hex Codes", "Stash", "#ffffff"));

            anchorMin = Convert.ToString(GetConfig("GUI", "Anchor Min", "0.667 0.020"));
            anchorMax = Convert.ToString(GetConfig("GUI", "Anchor Max", "0.810 0.148"));

            useGroupColors = Convert.ToBoolean(GetConfig("Group Limit", "Use Group Colors Configuration", true));
            groupColorDead = Convert.ToString(GetConfig("Group Limit", "Dead Color", "#ff0000"));
            groupColorBasic = Convert.ToString(GetConfig("Group Limit", "Group Color Basic", "#ffff00"));

            var _groupColors = GetConfig("Group Limit", "Group Colors", DefaultGroupColors) as List<object>;

            if (_groupColors != null && _groupColors.Count > 0)
            {
                SetupGroupColors(_groupColors);
            }

            szChatCommand = Convert.ToString(GetConfig("Settings", "Chat Command", "radar"));

            if (!string.IsNullOrEmpty(szChatCommand))
                cmd.AddChatCommand(szChatCommand, this, cmdESP);

            if (szChatCommand != "radar")
                cmd.AddChatCommand("radar", this, cmdESP);

            //voiceSymbol = Convert.ToString(GetConfig("Voice Detection", "Voice Symbol", "🔊"));
            useVoiceDetection = Convert.ToBoolean(GetConfig("Voice Detection", "Enabled", true));
            voiceInterval = Convert.ToInt32(GetConfig("Voice Detection", "Timeout After X Seconds", 3));
            voiceDistance = Convert.ToSingle(GetConfig("Voice Detection", "Detection Radius", 30f));

            if (Changed)
            {
                SaveConfig();
                Changed = false;
            }
        }

        protected override void LoadDefaultConfig()
        {
            PrintWarning("Creating a new configuration file");
            Config.Clear();
            LoadVariables();
        }

        object GetConfig(string menu, string datavalue, object defaultValue)
        {
            var data = Config[menu] as Dictionary<string, object>;
            if (data == null)
            {
                data = new Dictionary<string, object>();
                Config[menu] = data;
                Changed = true;
            }
            object value;
            if (!data.TryGetValue(datavalue, out value))
            {
                value = defaultValue;
                data[datavalue] = value;
                Changed = true;
            }
            return value;
        }

        string msg(string key, string id = null, params object[] args)
        {
            string message = id == null ? RemoveFormatting(lang.GetMessage(key, this, id)) : lang.GetMessage(key, this, id);

            return args.Length > 0 ? string.Format(message, args) : message;
        }

        string RemoveFormatting(string source) => source.Contains(">") ? System.Text.RegularExpressions.Regex.Replace(source, "<.*?>", string.Empty) : source;
        #endregion
    }
}