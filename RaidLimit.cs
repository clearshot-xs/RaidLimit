using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Raid Limit", "Clearshot", "1.3.0")]
    [Description("Limit the number of raids")]
    class RaidLimit : CovalencePlugin
    {
        //private PluginConfig _config;
        private Game.Rust.Libraries.Player _rustPlayer = Interface.Oxide.GetLibrary<Game.Rust.Libraries.Player>("Player");
        private PrefabToItemManager _prefabToItemManager = new PrefabToItemManager();

        private bool _wipeData = false;
        private Dictionary<ulong, HashSet<ulong>> _playerAssociations = new Dictionary<ulong, HashSet<ulong>>();
        private Dictionary<ulong, Dictionary<ulong, RaidLimitLog>> _raidLimits = new Dictionary<ulong, Dictionary<ulong, RaidLimitLog>>();
        private string _playerAssociationsFilename;
        private string _raidLimitsFilename;
        private bool _savePlayerAssociations;
        private bool _saveRaidLimits;
        private HashSet<ulong> _adminsWatching = new HashSet<ulong>();
        private HashSet<ulong> _activeUI = new HashSet<ulong>();

        private const int RAID_LIMIT_RESET_HOURS = 24;
        private const int RAID_LIMIT = 2;
        private const string RAID_LIMIT_UI = "RAID_LIMIT_UI";

        private Dictionary<ulong, float> _raidLimitCheckCooldown = new Dictionary<ulong, float>();
        private HashSet<string> _raidLimitCheckItems = new HashSet<string> {
            "hammer",
            "toolgun"
        };

        private HashSet<string> _raidLimitRaidItems = new HashSet<string> {
            "ammo.grenadelauncher.he",
            "ammo.rifle.explosive",
            "ammo.rocket.hv",
            "ammo.rocket.mlrs",
            "ammo.rocket.basic",
            "ammo.rocket.sam",
            "submarine.torpedo.rising",
            "submarine.torpedo.straight",
            "explosive.satchel",
            "explosive.timed"
            //"grenade.beancan",
            //"grenade.f1"
        };

        #region Hooks

        private void Init()
        {
            //permission.RegisterPermission("", this);
        }

        private void OnServerInitialized()
        {
            _prefabToItemManager.Init();

            _playerAssociationsFilename = $"{Name}\\PlayerAssociations";
            _raidLimitsFilename = $"{Name}\\RaidLimits";
            _playerAssociations = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, HashSet<ulong>>>(_playerAssociationsFilename);
            _raidLimits = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, Dictionary<ulong, RaidLimitLog>>>(_raidLimitsFilename);

            /*var codeLocks = BaseNetworkable.serverEntities
                .Where(e => e != null && e is CodeLock)
                .Select(e => e as CodeLock);

            if (codeLocks != null && codeLocks.Count() > 0)
            {
                //Dictionary<ulong, HashSet<ulong>> saveCodeLocks = new Dictionary<ulong, HashSet<ulong>>();
                Puts($"Found {codeLocks.Count()} code locks");
                foreach (var codeLock in codeLocks)
                {
                    if (codeLock == null) continue;

                    //saveCodeLocks[codeLock.net.ID] = new HashSet<ulong>();

                    foreach (ulong playerId in codeLock.whitelistPlayers)
                    {
                        //saveCodeLocks[codeLock.net.ID].Add(playerId);
                        AddAssociation(codeLock.OwnerID, playerId);
                    }

                    foreach (ulong playerId in codeLock.guestPlayers)
                    {
                        //saveCodeLocks[codeLock.net.ID].Add(playerId);
                        AddAssociation(codeLock.OwnerID, playerId);
                    }
                }

                //Interface.Oxide.DataFileSystem.WriteObject($"{Name}\\CodeLocks", saveCodeLocks);
            }*/

            //Dictionary<ulong, List<ulong>> saveTeams = new Dictionary<ulong, List<ulong>>();
            foreach (var team in RelationshipManager.ServerInstance.teams)
            {
                //saveTeams.Add(team.Value.teamID, team.Value.members);
                foreach (var playerId in team.Value.members)
                {
                    AddAssociation(team.Value.teamLeader, playerId);
                }
            }

            //Interface.Oxide.DataFileSystem.WriteObject($"{Name}\\Teams", saveTeams);

            foreach(var player in BasePlayer.activePlayerList)
            {
                CreateUI(player, RAID_LIMIT_UI);
            }

            timer.Every(300f, () => {
                HashSet<ulong> refreshUI = new HashSet<ulong>();
                int removed = 0;
                foreach(var entry in _raidLimits)
                {
                    HashSet<ulong> raidLimitsToRemove = new HashSet<ulong>();
                    foreach (var raidLimit in entry.Value)
                    {
                        if (DateTime.Now.Subtract(raidLimit.Value.timestamp).TotalHours >= RAID_LIMIT_RESET_HOURS)
                        {
                            raidLimitsToRemove.Add(raidLimit.Key);
                            refreshUI.Add(entry.Key);
                        }
                    }

                    foreach (var key in raidLimitsToRemove)
                    {
                        _raidLimits[entry.Key].Remove(key);
                        removed++;
                    }
                }

                HashSet<ulong> emptyToRemove = new HashSet<ulong>();
                foreach (var entry in _raidLimits)
                {
                    if (entry.Value.Count == 0)
                        emptyToRemove.Add(entry.Key);
                }

                foreach (var key in emptyToRemove)
                {
                    _raidLimits.Remove(key);
                }

                if (removed > 0)
                    Puts($"Removed {removed} raid limits older than {RAID_LIMIT_RESET_HOURS} hours");

                foreach (var id in refreshUI)
                {
                    var alliance = new HashSet<ulong>();
                    GetAlliance(id, alliance);

                    foreach (var ally in alliance)
                    {
                        var player = BasePlayer.FindByID(ally);
                        if (player == null || !player.IsAlive()) continue;

                        CreateUI(player, RAID_LIMIT_UI);
                        SendChatMsg(player, $"<color=#00a7fe>1 raid point has been reset!</color>");
                    }
                }

                _saveRaidLimits = _saveRaidLimits || removed > 0 || emptyToRemove.Count > 0;
                SavePlayerAssociations();
                SaveRaidLimits();
            });

            timer.Every(1f, () => {
                if (_adminsWatching.Count == 0) return;

                Dictionary<Color, HashSet<ulong>> alliances = GetOnlineUniqueAlliances();
                int count = 0;
                foreach (var alliance in alliances)
                {
                    foreach (var ally in alliance.Value)
                    {
                        var allyPlayer = BasePlayer.FindByID(ally);
                        if (allyPlayer == null || !allyPlayer.IsAlive()) continue;

                        foreach (var adminPlayer in BasePlayer.activePlayerList)
                        {
                            if (adminPlayer == null || !adminPlayer.IsAdmin || !adminPlayer.IsAlive()) continue;
                            if (!_adminsWatching.Contains(adminPlayer.userID)) continue;
                            if (Vector3.Distance(allyPlayer.transform.position, adminPlayer.transform.position) > 1000f) continue;

                            var pos = allyPlayer.transform.position;
                                pos.y += allyPlayer.GetHeight() - .5f;

                            Text(adminPlayer, pos, $"[{count}] {allyPlayer.displayName}", alliance.Key, 1);
                        }
                    }

                    count++;
                }

                /*var buildingPrivList = UnityEngine.Object.FindObjectsOfType<BuildingPrivlidge>();
                foreach (var buildingPriv in buildingPrivList)
                {
                    if (buildingPriv == null) continue;

                    foreach (var adminPlayer in BasePlayer.activePlayerList)
                    {
                        if (adminPlayer == null || !adminPlayer.IsAdmin || !adminPlayer.IsAlive()) continue;
                        if (!_adminsWatching.Contains(adminPlayer.userID)) continue;

                        var tcOwner = covalence.Players.FindPlayerById(buildingPriv.OwnerID.ToString());
                        if (tcOwner == null || buildingPriv.Distance(adminPlayer) > 500) continue;

                        Text(adminPlayer, buildingPriv.transform.position, $"{tcOwner.Name}", Color.magenta, 1);
                    }
                }*/
            });

            if (_wipeData)
            {
                Puts($"Wipe detected! Removing {_raidLimits.Count} raid limits.");
                _wipeData = false;
                _playerAssociations = new Dictionary<ulong, HashSet<ulong>>();
                _raidLimits = new Dictionary<ulong, Dictionary<ulong, RaidLimitLog>>();
                SavePlayerAssociations(true);
                SaveRaidLimits(true);
            }
        }

        private void Unload()
        {
            foreach (BasePlayer pl in BasePlayer.activePlayerList)
                if (pl != null && _activeUI.Contains(pl.userID))
                    CuiHelper.DestroyUi(pl, RAID_LIMIT_UI);

            SavePlayerAssociations(true);
            SaveRaidLimits(true);
        }

        private void OnNewSave(string filename)
        {
            _wipeData = true;
        }

        private void OnPlayerDisconnected(BasePlayer pl, string reason)
        {
            _adminsWatching.Remove(pl.userID);
            _activeUI.Remove(pl.userID);
            _raidLimitCheckCooldown.Remove(pl.userID);
        }

        private void OnPlayerSleepEnded(BasePlayer pl)
        {
            if (pl == null || pl.IsNpc)
                return;

            CreateUI(pl, RAID_LIMIT_UI);
        }

        /*private void OnCodeEntered(CodeLock codeLock, BasePlayer pl, string code)
        {
            if (codeLock == null || pl == null || pl.IsNpc)
                return;

            if (code == codeLock.code || code == codeLock.guestCode)
            {
                AddAssociation(codeLock.OwnerID, pl.userID);
                //AddAssociation(player.userID, codeLock.OwnerID);
            }
        }*/

        private void OnTeamAcceptInvite(RelationshipManager.PlayerTeam team, BasePlayer pl)
        {
            if (team == null || pl == null || pl.IsNpc)
                return;

            AddAssociation(team.teamLeader, pl.userID);
            //AddAssociation(player.userID, team.teamLeader);
        }

        private void OnPlayerInput(BasePlayer pl, InputState input)
        {
            if (pl == null || pl.IsNpc)
                return;

            float timeSinceStartup = Time.realtimeSinceStartup;
            float cooldown;
            if (_raidLimitCheckCooldown.TryGetValue(pl.userID, out cooldown) && cooldown > timeSinceStartup)
                return;

            if (!input.WasJustReleased(BUTTON.FIRE_PRIMARY))
                return;

            var heldEntity = pl.GetHeldEntity();
            if (heldEntity == null)
                return;

            if (!_raidLimitCheckItems.Contains(_prefabToItemManager.GetItemFromPrefabShortname(heldEntity.ShortPrefabName)))
                return;

            var entity = EyeTraceToEntity(pl, 150f);
            if (entity == null || Vector3.Distance(entity.transform.position, pl.transform.position) <= 3.5f)
                return;

            _raidLimitCheckCooldown[pl.userID] = timeSinceStartup + 1f;

            var buildingPriv = entity.GetBuildingPrivilege();
            if (buildingPriv == null)
            {
                SendChatMsg(pl, $"<color=#00FF00>FREE RAID, NO TC FOUND</color>");
                return;
            }

            var attackerAlliance = new HashSet<ulong>();
            GetAlliance(pl.userID, attackerAlliance);

            bool isAllyBuilding = buildingPriv.OwnerID == pl.userID;
            foreach (var attackerAlly in attackerAlliance)
            {
                if (isAllyBuilding)
                    break;

                isAllyBuilding = buildingPriv.OwnerID == attackerAlly;
            }

            if (isAllyBuilding)
            {
                SendChatMsg(pl, $"<color=#00FF00>RAID ALLOWED: ATTACKING OWN/ALLY BASE</color>");
                return;
            }

            RaidLimitLog raidLimitLog = null;
            bool isRaidFree = false;
            if (_raidLimits.ContainsKey(buildingPriv.OwnerID) && _raidLimits[buildingPriv.OwnerID].ContainsKey(pl.userID))
            {
                raidLimitLog = _raidLimits[buildingPriv.OwnerID][pl.userID];
                isRaidFree = true;
            }

            foreach (var attackerAlly in attackerAlliance)
            {
                if (isRaidFree)
                    break;

                if (_raidLimits.ContainsKey(buildingPriv.OwnerID) && _raidLimits[buildingPriv.OwnerID].ContainsKey(attackerAlly))
                {
                    raidLimitLog = _raidLimits[buildingPriv.OwnerID][attackerAlly];
                    isRaidFree = true;
                }
            }

            if (isRaidFree)
            {
                var ts = raidLimitLog.timestamp.AddHours(RAID_LIMIT_RESET_HOURS) - DateTime.Now;
                SendChatMsg(pl, $"<color=#00FF00>FREE RAID: ENEMY/ENEMY ALLY HAS ALREADY ATTACKED YOU\n\n{ts.Hours} hours and {ts.Minutes} minutes remaining</color>");
                return;
            }

            if (_raidLimits.ContainsKey(pl.userID))
            {
                var victimAlliance = new HashSet<ulong>();
                GetAlliance(buildingPriv.OwnerID, victimAlliance);

                bool hasRaidedVictim = false;
                foreach (var victimAlly in victimAlliance)
                {
                    if (hasRaidedVictim)
                        break;

                    hasRaidedVictim = _raidLimits[pl.userID].ContainsKey(victimAlly);
                }

                if (hasRaidedVictim)
                    SendChatMsg(pl, $"<color=#00FF00>RAID ALLOWED: ATTACKING SAME ENEMY/ENEMY ALLY BASE</color>");
                else if (_raidLimits[pl.userID].Count() >= RAID_LIMIT)
                    SendChatMsg(pl, $"<color=#FF0000>RAID BLOCKED: 0 RAID POINTS AVAILABLE!</color>");
                else
                    SendChatMsg(pl, $"<color=#CCCC00>RAID POSSIBLE: RAID WILL COST 1 RAID POINT</color>");
            }
            else
                SendChatMsg(pl, $"<color=#CCCC00>RAID POSSIBLE: RAID WILL COST 1 RAID POINT</color>");
        }

        private object OnEntityTakeDamage(DecayEntity entity, HitInfo info)
        {
            if (entity == null || info == null)
                return null;

            // ignore decay entities owned by the server
            if (entity.OwnerID == 0)
                return null;

            // ignore twig buildings
            var buildingBlock = entity as BuildingBlock;
            if (buildingBlock != null && buildingBlock.grade == BuildingGrade.Enum.Twigs)
                return null;

            var attacker = info.InitiatorPlayer;
            if (attacker == null || attacker.IsNpc)
                return null;

            var weaponPrefabShortname = info?.WeaponPrefab?.ShortPrefabName;
            var projectilePrefabShortname = info?.ProjectilePrefab?.name;
            var projectileItemShortname = projectilePrefabShortname != null ? _prefabToItemManager.GetItemFromPrefabShortname(projectilePrefabShortname) : null;

            // get weapon/projectile info from players held entity if HitInfo weapon/projectile is NULL
            var heldEntity = attacker.GetHeldEntity();
            if (heldEntity is AttackEntity && info.damageTypes != null && IsValidDamageType(info.damageTypes.GetMajorityDamageType()))
            {
                if (info.WeaponPrefab == null)
                {
                    weaponPrefabShortname = heldEntity.ShortPrefabName;

                    //PrintDebug($"OnEntityDeath - WeaponPrefab is NULL! Using HeldEntity: {heldEntity.ShortPrefabName ?? "NULL"}");
                }

                var projectile = heldEntity?.GetComponent<BaseProjectile>();
                var heldProjectileItemShortname = projectile?.primaryMagazine?.ammoType?.shortname ?? null;
                if ((info.WeaponPrefab == null && info.ProjectilePrefab == null)
                    || (projectileItemShortname != null && heldProjectileItemShortname != null && projectileItemShortname != heldProjectileItemShortname)) // certain projectiles from HitInfo do not match the projectile in the players gun Ex: ammo.pistol.hv, rifle.ammmo.hv, ammo.shotgun
                {
                    /*if (_debug)
                    {
                        if (projectileItemShortname != heldProjectileItemShortname)
                            PrintDebug($"OnEntityDeath - ProjectileItemShortname ({projectileItemShortname ?? "NULL"}) != HeldProjectileItemShortname ({heldProjectileItemShortname ?? "NULL"})! Using HeldProjectileItemShortname: {heldProjectileItemShortname ?? "NULL"}");
                        else
                            PrintDebug($"OnEntityDeath - WeaponPrefab + ProjectilePrefab are NULL! Using HeldEntityProjectile: {projectileItemShortname ?? "NULL"}");
                    }*/

                    projectileItemShortname = heldProjectileItemShortname;
                }
            }

            var weaponItemShortname = weaponPrefabShortname != null ? _prefabToItemManager.GetItemFromPrefabShortname(weaponPrefabShortname) : null;
            //SendChatMsg(attacker, $"Weapon: {weaponPrefabShortname ?? "NULL"}[{weaponItemShortname ?? "NULL"}], Projectile: {projectilePrefabShortname ?? "NULL"}[{projectileItemShortname ?? "NULL"}]");
            //SendChatMsg(attacker, $"{info?.damageTypes?.GetMajorityDamageType()}");

            BuildingPrivlidge buildingPriv = entity.GetBuildingPrivilege();
            if (buildingPriv != null)
            {
                //Arrow(attacker, entity.WorldSpaceBounds().ToBounds().center, buildingPriv.WorldSpaceBounds().ToBounds().center, .1f, Color.magenta, 15);
                //Text(attacker, buildingPriv.transform.position, "TC", Color.magenta, 15);

                var attackerAlliance = new HashSet<ulong>();
                GetAlliance(attacker.userID, attackerAlliance);

                bool isAllyBuilding = buildingPriv.OwnerID == attacker.userID;
                foreach (var attackerAlly in attackerAlliance)
                {
                    if (isAllyBuilding)
                        break;

                    isAllyBuilding = buildingPriv.OwnerID == attackerAlly;
                }

                if (isAllyBuilding)
                {
                    //SendChatMsg(attacker, $"<color=#00FF00>[{entity.ShortPrefabName}][{entity.OwnerID}] RAID ALLOWED: ATTACKING OWN/ALLY BASE</color>");
                    return null;
                }

                bool isRaidFree = _raidLimits.ContainsKey(buildingPriv.OwnerID) && _raidLimits[buildingPriv.OwnerID].ContainsKey(attacker.userID);
                foreach (var attackerAlly in attackerAlliance)
                {
                    if (isRaidFree)
                        break;

                    isRaidFree = _raidLimits.ContainsKey(buildingPriv.OwnerID) && _raidLimits[buildingPriv.OwnerID].ContainsKey(attackerAlly);
                }

                if (isRaidFree)
                {
                    //SendChatMsg(attacker, $"<color=#00FF00>FREE RAID: ENEMY/ENEMY ALLY HAS ALREADY ATTACKED YOU</color>");
                    return null;
                }

                var victimAlliance = new HashSet<ulong>();
                GetAlliance(buildingPriv.OwnerID, victimAlliance);

                if (_raidLimits.ContainsKey(attacker.userID))
                {
                    bool hasRaidedVictim = false; //!_raidLimits[attacker.userID].ContainsKey(buildingPriv.OwnerID);
                    foreach (var victimAlly in victimAlliance)
                    {
                        if (hasRaidedVictim)
                            break;

                        hasRaidedVictim = _raidLimits[attacker.userID].ContainsKey(victimAlly);
                    }

                    if (hasRaidedVictim)
                    {
                        //SendChatMsg(attacker, $"<color=#00FF00>[{entity.ShortPrefabName}][{entity.OwnerID}] RAID ALLOWED: ATTACKING SAME ENEMY/ENEMY ALLY BASE</color>");
                        return null;
                    }
                    else if (_raidLimits[attacker.userID].Count() >= RAID_LIMIT)
                    {
                        //SendChatMsg(attacker, $"<color=#FF0000>[{entity.ShortPrefabName}][{entity.OwnerID}] RAID BLOCKED: 0 RAID POINTS!</color>");
                        float timeSinceStartup = Time.realtimeSinceStartup;
                        float cooldown;
                        if (_raidLimitCheckCooldown.TryGetValue(attacker.userID, out cooldown) && cooldown > timeSinceStartup)
                            return false;

                        SendToast(attacker, 1, "Raid blocked! Type /rlc to check your raid limit cooldowns.");
                        _raidLimitCheckCooldown[attacker.userID] = timeSinceStartup + 1f;
                        return false;
                    }
                    //else
                        //SendChatMsg(attacker, $"<color=#CCCC00>[{entity.ShortPrefabName}][{entity.OwnerID}] RAID POSSIBLE: RAID WILL COST 1 RAID POINT</color>");
                }

                if (_raidLimitRaidItems.Contains(weaponItemShortname) || _raidLimitRaidItems.Contains(projectileItemShortname) 
                    || info?.damageTypes?.GetMajorityDamageType() == Rust.DamageType.Explosion)
                {
                    if (!_raidLimits.ContainsKey(attacker.userID))
                        _raidLimits[attacker.userID] = new Dictionary<ulong, RaidLimitLog>();

                    bool hasRaidedVictim = false;
                    foreach (var victimAlly in victimAlliance)
                    {
                        if (hasRaidedVictim)
                            break;

                        hasRaidedVictim = _raidLimits[attacker.userID].ContainsKey(victimAlly);
                    }

                    if (!hasRaidedVictim)
                    {
                        foreach (var attackerAlly in attackerAlliance)
                        {
                            if (!_raidLimits.ContainsKey(attackerAlly))
                                _raidLimits[attackerAlly] = new Dictionary<ulong, RaidLimitLog>();

                            if (!_raidLimits[attackerAlly].ContainsKey(buildingPriv.OwnerID))
                            {
                                _raidLimits[attackerAlly].Add(buildingPriv.OwnerID, new RaidLimitLog {
                                    attacker = attacker.userID,
                                    position = buildingPriv.transform.position,
                                    timestamp = DateTime.Now,
                                });
                            }

                            var allyPlayer = BasePlayer.FindByID(attackerAlly);
                            if (allyPlayer != null && allyPlayer.IsAlive())
                            {
                                CreateUI(allyPlayer, RAID_LIMIT_UI);
                                SendChatMsg(allyPlayer, $"<color=#00FF00>{attacker.displayName} used 1 raid point @ {PhoneController.PositionToGridCoord(buildingPriv.transform.position)}</color>");
                            }
                        }

                        _saveRaidLimits = true;
                    }
                    //else
                        //SendChatMsg(attacker, $"<color=#00FF00>[{entity.ShortPrefabName}][{entity.OwnerID}] RAID ALLOWED: ATTACKING ENEMY/ENEMY ALLY BASE</color>");
                }

                // block damage if raid not started
                // return false;
            }
            //else
                //SendChatMsg(attacker, $"<color=#00FF00>[{entity.ShortPrefabName}][{entity.OwnerID}] FREE RAID, NO TC FOUND</color>");

            return null;
        }

        /*private object OnCupboardAuthorize(BuildingPrivlidge privilege, BasePlayer player)
        {
            Puts("OnCupboardAuthorize works!");
            return null;
        }

        private void OnEntityBuilt(Planner plan, GameObject go)
        {
            Puts("OnEntityBuilt works!");
        }

        private object OnConstructionPlace(BaseEntity entity, Construction component, Construction.Target constructionTarget, BasePlayer player)
        {
            Puts("OnConstructionPlace works!");
            return null;
        }*/

        #endregion

        #region Commands

        private string[] _uniqueHexColors = new string[] {
            "#01FFFE", "#FFA6FE", "#FFDB66", "#006401", "#010067",
            "#95003A", "#007DB5", "#FF00F6", "#FFEEE8", "#774D00",
            "#90FB92", "#0076FF", "#D5FF00", "#FF937E", "#6A826C",
            "#FF029D", "#FE8900", "#7A4782", "#7E2DD2", "#85A900",
            "#FF0056", "#A42400", "#00AE7E", "#683D3B", "#BDC6FF",
            "#263400", "#BDD393", "#00B917", "#9E008E", "#001544",
            "#C28C9F", "#FF74A3", "#01D0FF", "#004754", "#E56FFE",
            "#788231", "#0E4CA1", "#91D0CB", "#BE9970", "#968AE8",
            "#BB8800", "#43002C", "#DEFF74", "#00FFC6", "#FFE502",
            "#620E00", "#008F9C", "#98FF52", "#7544B1", "#B500FF",
            "#00FF78", "#FF6E41", "#005F39", "#6B6882", "#5FAD4E",
            "#A75740", "#A5FFD2", "#FFB167", "#009BFF", "#E85EBE",
            "#00FF00", "#0000FF", "#FF0000", "#000000"
        };

        /*[Command("rl.view")]
        private void PlayerAssociationsCommand(IPlayer player, string command, string[] args)
        {
            BasePlayer pl = player.Object as BasePlayer;
            if (pl == null || !pl.IsAdmin) return;

            Dictionary<Color, HashSet<ulong>> alliances = GetOnlineUniqueAlliances();
            int count = 0;
            foreach (var alliance in alliances)
            {
                foreach (var ally in alliance.Value)
                {
                    var allyPlayer = BasePlayer.FindByID(ally);
                    if (allyPlayer == null || !allyPlayer.IsAlive()) continue;

                    var pos = allyPlayer.transform.position;
                    pos.y += allyPlayer.GetHeight() - .5f;

                    Text(pl, pos, $"[{count}] {allyPlayer.displayName}", alliance.Key, 1);
                }

                count++;
            }
        }*/

        [Command("rl.debug")]
        private void PlayerAssociationsDebugCommand(IPlayer player, string command, string[] args)
        {
            if (!player.IsServer) return;

            Interface.Oxide.DataFileSystem.WriteObject($"{Name}\\AllUniqueAlliances", GetAllUniqueAlliances());
            Interface.Oxide.DataFileSystem.WriteObject($"{Name}\\OnlineUniqueAlliances", GetOnlineUniqueAlliances());
        }

        [Command("rl.live")]
        private void ViewAssociationsCommand(IPlayer player, string command, string[] args)
        {
            BasePlayer pl = player.Object as BasePlayer;
            if (pl == null || !pl.IsAdmin) return;

            if (_adminsWatching.Contains(pl.userID))
                _adminsWatching.Remove(pl.userID);
            else
                _adminsWatching.Add(pl.userID);
        }

        [Command("rl.reset")]
        private void ResetRaidLimitCommand(IPlayer player, string command, string[] args)
        {
            if (player == null || !player.IsServer) return;

            Puts($"Removing {_raidLimits.Count} raid limits");
            _raidLimits = new Dictionary<ulong, Dictionary<ulong, RaidLimitLog>>();
            SaveRaidLimits(true);
        }

        /*[Command("rl.save")]
        private void SaveAssociationsCommand(IPlayer player, string command, string[] args)
        {
            BasePlayer pl = player.Object as BasePlayer;
            if (pl == null || !pl.IsAdmin) return;

            Interface.Oxide.DataFileSystem.WriteObject($"{Name}\\PlayerAssociations", _playerAssociations);
        }*/

        [Command("rlgroup", "rlg", "rla")]
        private void RaidLimitAssociationsCommand(IPlayer player, string command, string[] args)
        {
            BasePlayer pl = player.Object as BasePlayer;
            if (pl == null || !pl.IsAdmin) return;

            var alliance = new HashSet<ulong>();
            GetAlliance(pl.userID, alliance);

            IEnumerable<string> allianceNames = alliance.Select(x => covalence.Players.FindPlayerById(x.ToString()).Name ?? "Unknown").OrderBy(x => x);
            SendChatMsg(pl, $"<size=16><color=#00a7fe>{pl.displayName}'s Raid Limit Associations</color></size>\n\n{string.Join(", ", allianceNames)}\n\n<color=#e16969><size=12>Associations last for the entire wipe and can't be removed. Be aware of who you associate with through teams and base building!</size></color>");
        }

        [Command("rlcheck", "rlc")]
        private void RaidLimitCheckCommand(IPlayer player, string command, string[] args)
        {
            BasePlayer pl = player.Object as BasePlayer;
            if (pl == null) return;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<size=16><color=#00a7fe>Raid Limit Cooldown Check</color></size>\n");

            Dictionary<ulong, RaidLimitLog> raidLimitLog;
            if (!_raidLimits.TryGetValue(pl.userID, out raidLimitLog) || raidLimitLog.Count == 0)
            {
                sb.AppendLine($"No raid cooldowns found, you have <color=#00a7fe>{RAID_LIMIT} raids</color> available!");
                SendChatMsg(pl, sb.ToString());
                return;
            }

            foreach(var entry in raidLimitLog)
            {
                var attackerName = covalence.Players.FindPlayerById(entry.Value.attacker.ToString()).Name ?? "Unknown";
                sb.AppendLine($"<color=#00a7fe>{attackerName}</color> raided a base in <color=#00a7fe>{PhoneController.PositionToGridCoord(entry.Value.position)}</color> <color=#9A79FF>{GetElapsedTime(DateTime.Now - entry.Value.timestamp)}</color>");
            }

            sb.AppendLine($"\nRaid points are automatically reset after <color=#00a7fe>{RAID_LIMIT_RESET_HOURS} hours</color>");

            SendChatMsg(pl, sb.ToString());
        }

        private string GetElapsedTime(TimeSpan ts)
        {
            string ret = "";
            if (ts.Hours > 0)
                ret += $"{ts.Hours} hours";
            else if (ts.Minutes > 0)
                ret += $"{ts.Minutes} minutes";
            else if (ts.Seconds > 0)
                ret += $"{ts.Seconds} seconds";

            return ret += $" ago";
        }

        #endregion

        #region UI

        private void CreateUI(BasePlayer pl, string name)
        {
            if (_activeUI.Contains(pl.userID))
            {
                _activeUI.Remove(pl.userID);
                CuiHelper.DestroyUi(pl, name);
            }

            Dictionary<ulong, RaidLimitLog> raidLimit;
            if (!_raidLimits.TryGetValue(pl.userID, out raidLimit))
                raidLimit = new Dictionary<ulong, RaidLimitLog>();

            var limit = RAID_LIMIT - raidLimit.Count;
            var elements = new CuiElementContainer {
                {
                    new CuiPanel
                    {
                        RectTransform = {
                            AnchorMin = "0.5 0.0",
                            AnchorMax = "0.5 0.0",
                            OffsetMin = "-278 18",
                            OffsetMax = "-202 38"
                        },
                        Image = {
                            Color = "0.0 0.0 0.0 0.7"
                        },
                        CursorEnabled = false
                    },
                    "Hud",
                    name
                },
                {
                    new CuiLabel {
                        RectTransform = {
                            AnchorMin = "0.0 0.0",
                            AnchorMax = "1.0 1.0",
                        },
                        Text = {
                            Align = TextAnchor.MiddleCenter,
                            Text = $"RAID LIMIT: {limit} / {RAID_LIMIT}",
                            Color = "1.0 1.0 1.0 1.0",
                            FontSize = 10
                        }
                    },
                    name,
                    CuiHelper.GetGuid()
                }
            };

            _activeUI.Add(pl.userID);
            CuiHelper.AddUi(pl, elements);
        }

        #endregion

        #region Association Helpers

        private void AddAssociation(ulong ownerId, ulong targetId)
        {
            if (ownerId == targetId) return;

            if (!_playerAssociations.ContainsKey(ownerId))
                _playerAssociations[ownerId] = new HashSet<ulong>();

            if (!_playerAssociations.ContainsKey(targetId))
                _playerAssociations[targetId] = new HashSet<ulong>();

            Dictionary<ulong, RaidLimitLog> ownerRaidLimit;
            if (!_raidLimits.TryGetValue(ownerId, out ownerRaidLimit))
                ownerRaidLimit = new Dictionary<ulong, RaidLimitLog>();

            Dictionary<ulong, RaidLimitLog> targetRaidLimit;
            if (!_raidLimits.TryGetValue(targetId, out targetRaidLimit))
                targetRaidLimit = new Dictionary<ulong, RaidLimitLog>();

            if (ownerRaidLimit.Count > 0 || targetRaidLimit.Count > 0)
            {
                if (ownerRaidLimit.Count > targetRaidLimit.Count)
                {
                    // copy owner raid limits into player and player allies raid limits
                    _raidLimits[targetId] = ownerRaidLimit;

                    var targetAlliance = new HashSet<ulong>();
                    GetAlliance(targetId, targetAlliance);

                    foreach (var ally in targetAlliance)
                    {
                        _raidLimits[ally] = ownerRaidLimit;

                        var allyPlayer = BasePlayer.FindByID(ally);
                        if (allyPlayer != null && allyPlayer.IsAlive())
                        {
                            CreateUI(allyPlayer, RAID_LIMIT_UI);
                            //SendChatMsg(allyPlayer, $"#1: Merging raid limits [New Ally: {ownerRaidLimit.Count} > Self: {targetRaidLimit.Count}]");
                        }
                    }

                    var ownerAlliance = new HashSet<ulong>();
                    GetAlliance(ownerId, ownerAlliance);

                    foreach (var ally in ownerAlliance)
                    {
                        var allyPlayer = BasePlayer.FindByID(ally);
                        if (allyPlayer != null && allyPlayer.IsAlive())
                        {
                            CreateUI(allyPlayer, RAID_LIMIT_UI);
                            //SendChatMsg(allyPlayer, $"#1: Merging raid limits [New Ally: {ownerRaidLimit.Count} > Self: {targetRaidLimit.Count}]");
                        }
                    }
                }
                else if (targetRaidLimit.Count > ownerRaidLimit.Count)
                {
                    // copy player raid limits into owner and owner allies raid limits
                    _raidLimits[ownerId] = targetRaidLimit;

                    var ownerAlliance = new HashSet<ulong>();
                    GetAlliance(ownerId, ownerAlliance);

                    foreach (var ally in ownerAlliance)
                    {
                        _raidLimits[ally] = targetRaidLimit;

                        var allyPlayer = BasePlayer.FindByID(ally);
                        if (allyPlayer != null && allyPlayer.IsAlive())
                        {
                            CreateUI(allyPlayer, RAID_LIMIT_UI);
                            //SendChatMsg(allyPlayer, $"#2: Merging raid limits [Self: {targetRaidLimit.Count} > New Ally: {ownerRaidLimit.Count}]");
                        }
                    }

                    var targetAlliance = new HashSet<ulong>();
                    GetAlliance(targetId, targetAlliance);

                    foreach (var ally in targetAlliance)
                    {
                        var allyPlayer = BasePlayer.FindByID(ally);
                        if (allyPlayer != null && allyPlayer.IsAlive())
                        {
                            CreateUI(allyPlayer, RAID_LIMIT_UI);
                            //SendChatMsg(allyPlayer, $"#2: Merging raid limits [Self: {targetRaidLimit.Count} > New Ally: {ownerRaidLimit.Count}]");
                        }
                    }
                }
                else
                {
                    // find oldest raid limit from owner OR player and copy to owner OR player and allies
                    DateTime oldestOwnerRaid = DateTime.Now;
                    if (_raidLimits.ContainsKey(ownerId))
                        oldestOwnerRaid = _raidLimits[ownerId].OrderByDescending(x => x.Value.timestamp).Select(x => x.Value.timestamp).First();

                    DateTime oldestPlayerRaid = DateTime.Now;
                    if (_raidLimits.ContainsKey(targetId))
                        oldestPlayerRaid = _raidLimits[targetId].OrderByDescending(x => x.Value.timestamp).Select(x => x.Value.timestamp).First();

                    if (oldestOwnerRaid > oldestPlayerRaid)
                    {
                        _raidLimits[targetId] = ownerRaidLimit;

                        var targetAlliance = new HashSet<ulong>();
                        GetAlliance(targetId, targetAlliance);

                        foreach (var ally in targetAlliance)
                        {
                            _raidLimits[ally] = ownerRaidLimit;

                            var allyPlayer = BasePlayer.FindByID(ally);
                            if (allyPlayer != null && allyPlayer.IsAlive())
                            {
                                CreateUI(allyPlayer, RAID_LIMIT_UI);
                                //SendChatMsg(allyPlayer, $"#3: Merging raid limits [New Ally: {oldestOwnerRaid} > Self: {oldestPlayerRaid}]");
                            }
                        }

                        var ownerAlliance = new HashSet<ulong>();
                        GetAlliance(ownerId, ownerAlliance);

                        foreach (var ally in ownerAlliance)
                        {
                            var allyPlayer = BasePlayer.FindByID(ally);
                            if (allyPlayer != null && allyPlayer.IsAlive())
                            {
                                CreateUI(allyPlayer, RAID_LIMIT_UI);
                                //SendChatMsg(allyPlayer, $"#3: Merging raid limits [New Ally: {oldestOwnerRaid} > Self: {oldestPlayerRaid}]");
                            }
                        }
                    }
                    else if (oldestPlayerRaid > oldestOwnerRaid)
                    {
                        _raidLimits[ownerId] = targetRaidLimit;

                        var ownerAlliance = new HashSet<ulong>();
                        GetAlliance(ownerId, ownerAlliance);

                        foreach (var ally in ownerAlliance)
                        {
                            _raidLimits[ally] = targetRaidLimit;

                            var allyPlayer = BasePlayer.FindByID(ally);
                            if (allyPlayer != null && allyPlayer.IsAlive())
                            {
                                CreateUI(allyPlayer, RAID_LIMIT_UI);
                                //SendChatMsg(allyPlayer, $"#4: Merging raid limits [Self: {oldestPlayerRaid} > New Ally: {oldestOwnerRaid}]");
                            }
                        }

                        var targetAlliance = new HashSet<ulong>();
                        GetAlliance(targetId, targetAlliance);

                        foreach (var ally in targetAlliance)
                        {
                            var allyPlayer = BasePlayer.FindByID(ally);
                            if (allyPlayer != null && allyPlayer.IsAlive())
                            {
                                CreateUI(allyPlayer, RAID_LIMIT_UI);
                                //SendChatMsg(allyPlayer, $"#4: Merging raid limits [Self: {oldestPlayerRaid} > New Ally: {oldestOwnerRaid}]");
                            }
                        }
                    }
                }
            }

            _playerAssociations[ownerId].Add(targetId);
            _playerAssociations[targetId].Add(ownerId);
            _savePlayerAssociations = true;
        }

        private Dictionary<Color, HashSet<ulong>> GetOnlineUniqueAlliances()
        {
            var alliances = new Dictionary<Color, HashSet<ulong>>();
            var visited = new HashSet<ulong>();

            foreach (var activePlayer in BasePlayer.activePlayerList)
            {
                if (activePlayer == null || visited.Contains(activePlayer.userID))
                    continue;

                var alliance = new HashSet<ulong>();
                var hexColor = _uniqueHexColors[alliances.Count];

                Color color;
                if (!ColorUtility.TryParseHtmlString(hexColor, out color))
                    color = GetRandomColor();

                GetAlliance(activePlayer.userID, alliance, visited);
                alliances[color] = alliance;
            }

            return alliances;
        }

        private Dictionary<Color, HashSet<ulong>> GetAllUniqueAlliances()
        {
            var alliances = new Dictionary<Color, HashSet<ulong>>();
            var visited = new HashSet<ulong>();

            foreach (var player in _playerAssociations)
            {
                if (visited.Contains(player.Key))
                    continue;

                var alliance = new HashSet<ulong>();
                var hexColor = _uniqueHexColors[alliances.Count];

                Color color;
                if (!ColorUtility.TryParseHtmlString(hexColor, out color))
                    color = GetRandomColor();

                GetAlliance(player.Key, alliance, visited);
                alliances[color] = alliance;
            }

            return alliances;
        }

        private void GetAlliance(ulong playerId, HashSet<ulong> alliance, HashSet<ulong> visited)
        {
            HashSet<ulong> allies;
            if (!_playerAssociations.TryGetValue(playerId, out allies))
                allies = new HashSet<ulong>();

            visited.Add(playerId);
            alliance.Add(playerId);
            foreach (var ally in allies)
            {
                if (visited.Contains(ally))
                    continue;

                GetAlliance(ally, alliance, visited);
            }
        }

        private void GetAlliance(ulong playerId, HashSet<ulong> alliance)
        {
            HashSet<ulong> allies;
            if (!_playerAssociations.TryGetValue(playerId, out allies))
                allies = new HashSet<ulong>();

            alliance.Add(playerId);
            foreach (var ally in allies)
            {
                if (alliance.Contains(ally))
                    continue;

                GetAlliance(ally, alliance);
            }
        }

        #endregion

        #region Helpers

        private void SendChatMsg(BasePlayer pl, string msg, string prefix = null) =>
            _rustPlayer.Message(pl, msg, prefix != null ? prefix : "", 0, Array.Empty<object>());
            //_rustPlayer.Message(pl, msg, prefix != null ? prefix : lang.GetMessage("ChatPrefix", this, pl.UserIDString), Convert.ToUInt64(_config.chatIconID), Array.Empty<object>());

        private void SendToast(BasePlayer pl, int style, string text)
        {
            pl.SendConsoleCommand("gametip.showtoast", new object[] {
                style,
                text
            });
        }

        private BaseEntity EyeTraceToEntity(BasePlayer pl, float distance, int mask = ~0)
        {
            RaycastHit hit;
            return Physics.Raycast(pl.eyes.HeadRay(), out hit, distance, mask) ? hit.GetEntity() : null;
        }

        public void Line(BasePlayer player, Vector3 from, Vector3 to, Color color, float duration) =>
            player.SendConsoleCommand("ddraw.line", duration, color, from, to);

        public void Arrow(BasePlayer player, Vector3 from, Vector3 to, float headSize, Color color, float duration) =>
            player.SendConsoleCommand("ddraw.arrow", duration, color, from, to, headSize);

        public void Sphere(BasePlayer player, Vector3 pos, float radius, Color color, float duration) =>
            player.SendConsoleCommand("ddraw.sphere", duration, color, pos, radius);

        public void Box(BasePlayer player, Vector3 pos, float size, Color color, float duration) =>
            player.SendConsoleCommand("ddraw.box", duration, color, pos, size);

        public void Text(BasePlayer player, Vector3 pos, string text, Color color, float duration) =>
            player.SendConsoleCommand("ddraw.text", duration, color, pos, text);

        private Color GetRandomColor() =>
            UnityEngine.Random.ColorHSV(0f, 1f, .4f, .8f, .5f, 1f);

        private bool IsValidDamageType(Rust.DamageType dmgType)
        {
            return dmgType == Rust.DamageType.Arrow || dmgType == Rust.DamageType.Blunt || dmgType == Rust.DamageType.Bullet
                || dmgType == Rust.DamageType.Explosion || dmgType == Rust.DamageType.Slash || dmgType == Rust.DamageType.Stab;
        }

        private void SavePlayerAssociations(bool forceSave = false)
        {
            if (!forceSave && !_savePlayerAssociations)
                return;

            _savePlayerAssociations = false;
            Interface.Oxide.DataFileSystem.WriteObject(_playerAssociationsFilename, _playerAssociations);
        }

        private void SaveRaidLimits(bool forceSave = false)
        {
            if (!forceSave && !_saveRaidLimits)
                return;

            _saveRaidLimits = false;
            Interface.Oxide.DataFileSystem.WriteObject(_raidLimitsFilename, _raidLimits);
        }

        #endregion

        #region Config
        /*
        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["ChatPrefix"] = $"<color=#00a7fe>[{Title}]</color>"
            }, this);
        }

        protected override void LoadDefaultConfig()
        {
            _config = GetDefaultConfig();
        }

        private PluginConfig GetDefaultConfig()
        {
            return new PluginConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            _config = Config.ReadObject<PluginConfig>();
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        private class PluginConfig
        {
            public string chatIconID = "0";
        }*/

        #endregion

        private class RaidLimitLog
        {
            public ulong attacker;
            public Vector3 position;
            public DateTime timestamp;
        }

        private class PrefabToItemManager
        {
            public Dictionary<string, string> prefabToItem = new Dictionary<string, string>();

            public void Init()
            {
                foreach (var item in ItemManager.itemList.OrderBy(x => x.shortname))
                {
                    ItemModEntity itemModEnt = item.GetComponent<ItemModEntity>();
                    if (itemModEnt != null)
                    {
                        var gameObjRef = itemModEnt.entityPrefab;
                        if (string.IsNullOrEmpty(gameObjRef.guid)) continue;

                        AddPrefabToItem(gameObjRef.resourcePath, item.shortname, nameof(ItemModEntity));
                    }

                    ItemModDeployable itemModDeploy = item.GetComponent<ItemModDeployable>();
                    if (itemModDeploy != null)
                    {
                        var gameObjRef = itemModDeploy.entityPrefab;
                        if (string.IsNullOrEmpty(gameObjRef.guid)) continue;

                        AddPrefabToItem(gameObjRef.resourcePath, item.shortname, nameof(ItemModDeployable));
                    }

                    ItemModProjectile itemModProj = item.GetComponent<ItemModProjectile>();
                    if (itemModProj != null)
                    {
                        var gameObjRef = itemModProj.projectileObject;
                        if (string.IsNullOrEmpty(gameObjRef.guid)) continue;

                        AddPrefabToItem(gameObjRef.resourcePath, item.shortname, nameof(ItemModProjectile));
                    }
                }

                foreach (var prefab in GameManifest.Current.entities)
                {
                    var gameObj = GameManager.server.FindPrefab(prefab);
                    if (gameObj == null) continue;

                    var thrownWep = gameObj.GetComponent<ThrownWeapon>();
                    if (thrownWep != null)
                    {
                        var itemShortname = GetItemFromPrefabShortname(thrownWep.ShortPrefabName); // get item shortname from held entity
                        AddPrefabToItem(thrownWep.prefabToThrow.resourcePath, itemShortname, nameof(ThrownWeapon)); // assign deployed entity to same item shortname

                        //PrintDebug($"  {thrownWep.ShortPrefabName}[{thrownWep.GetType()}] -> {GetPrefabShortname(thrownWep.prefabToThrow.resourcePath)} -> {itemShortname}");
                    }
                }
            }

            private void AddPrefabToItem(string prefab, string itemShortname, string prefabSource)
            {
                var prefabShortname = GetPrefabShortname(prefab);
                if (prefabToItem.ContainsKey(prefabShortname)) return;

                prefabToItem[prefabShortname] = itemShortname;
                //PrintDebug($"prefabToItem - {prefabSource}: {prefabShortname} -> {itemShortname}");
            }

            public string GetItemFromPrefabShortname(string prefabShortname) =>
                prefabToItem.ContainsKey(prefabShortname) ? prefabToItem[prefabShortname] : prefabShortname;

            public string GetPrefabShortname(string prefab) =>
                prefab.Substring(prefab.LastIndexOf('/') + 1).Replace(".prefab", "");

            public string GetPrettyItemName(string itemShortname) =>
                ItemManager.FindItemDefinition(itemShortname)?.displayName?.english ?? itemShortname;
        }
    }
}
