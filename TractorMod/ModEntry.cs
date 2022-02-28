using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Pathoschild.Stardew.Common;
using Pathoschild.Stardew.Common.Utilities;
using Pathoschild.Stardew.TractorMod.Framework;
using Pathoschild.Stardew.TractorMod.Framework.Attachments;
using Pathoschild.Stardew.TractorMod.Framework.Config;
using Pathoschild.Stardew.TractorMod.Framework.ModAttachments;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.Characters;
using StardewValley.GameData;
using StardewValley.Locations;

namespace Pathoschild.Stardew.TractorMod
{
    /// <summary>The mod entry point.</summary>
    internal class ModEntry : Mod, IAssetEditor, IAssetLoader
    {
        /*********
        ** Fields
        *********/
        /****
        ** Constants
        ****/
        /// <summary>The update rate when only one player is in a location (as a frame multiple).</summary>
        private readonly uint TextureUpdateRateWithSinglePlayer = 30;

        /// <summary>The update rate when multiple players are in the same location (as a frame multiple). This should be more frequent due to sprite broadcasts, new horses instances being created during NetRef&lt;Horse&gt; syncs, etc.</summary>
        private readonly uint TextureUpdateRateWithMultiplePlayers = 3;

        /// <summary>The unique ID for the stable building in <c>Data/BuildingsData</c>.</summary>
        private readonly string GarageBuildingId = "Pathoschild.TractorMod_Stable";

        /// <summary>The minimum version the host must have for the mod to be enabled on a farmhand.</summary>
        private readonly string MinHostVersion = "4.15.0";

        /// <summary>The base path for assets loaded through the game's content pipeline so other mods can edit them.</summary>
        private readonly string PublicAssetBasePath = "Mods/Pathoschild.TractorMod";

        /// <summary>The message ID for a request to warp a tractor to the given farmhand.</summary>
        private readonly string RequestTractorMessageID = "TractorRequest";

        /// <summary>A case-insensitive list of files in the <c>assets</c> folder.</summary>
        private readonly IDictionary<string, string> AssetMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /****
        ** State
        ****/
        /// <summary>The mod settings.</summary>
        private ModConfig Config;

        /// <summary>The configured key bindings.</summary>
        private ModConfigKeys Keys => this.Config.Controls;

        /// <summary>The backing field for <see cref="TractorManager"/>.</summary>
        private PerScreen<TractorManager> TractorManagerImpl;

        /// <summary>The tractor being ridden by the current player.</summary>
        private TractorManager TractorManager => this.TractorManagerImpl.Value;

        /// <summary>The garage texture to apply.</summary>
        private Texture2D GarageTexture;

        /// <summary>The tractor texture to apply.</summary>
        private Texture2D TractorTexture;

        /// <summary>The buff icon texture.</summary>
        private Texture2D BuffIconTexture;

        /// <summary>Whether the mod is enabled for the current farmhand.</summary>
        private bool IsEnabled = true;


        /*********
        ** Public methods
        *********/
        /// <summary>The mod entry point, called after the mod is first loaded.</summary>
        /// <param name="helper">Provides simplified APIs for writing mods.</param>
        public override void Entry(IModHelper helper)
        {
            // read config
            this.Config = helper.ReadConfig<ModConfig>();

            // init
            I18n.Init(helper.Translation);
            this.TractorManagerImpl = new(() =>
            {
                var manager = new TractorManager(this.Config, this.Keys, this.Helper.Reflection, () => this.BuffIconTexture);
                this.UpdateConfigFor(manager);
                return manager;
            });
            this.UpdateConfig();
            this.InitAssetMap();

            // hook events
            IModEvents events = helper.Events;
            events.GameLoop.GameLaunched += this.OnGameLaunched;
            events.GameLoop.SaveLoaded += this.OnSaveLoaded;
            events.GameLoop.DayStarted += this.OnDayStarted;
            events.GameLoop.DayEnding += this.OnDayEnding;
            events.GameLoop.Saved += this.OnSaved;
            events.Display.RenderedWorld += this.OnRenderedWorld;
            events.Input.ButtonsChanged += this.OnButtonsChanged;
            events.World.NpcListChanged += this.OnNpcListChanged;
            events.World.LocationListChanged += this.OnLocationListChanged;
            events.GameLoop.UpdateTicked += this.OnUpdateTicked;
            events.Multiplayer.ModMessageReceived += this.OnModMessageReceived;
            events.Player.Warped += this.OnWarped;

            LocalizedContentManager.OnLanguageChange += this.OnLanguageChange;

            // validate translations
            if (!helper.Translation.GetTranslations().Any())
                this.Monitor.Log("The translation files in this mod's i18n folder seem to be missing. The mod will still work, but you'll see 'missing translation' messages. Try reinstalling the mod to fix this.", LogLevel.Warn);
        }

        /// <inheritdoc />
        public bool CanLoad<T>(IAssetInfo asset)
        {
            return
                asset.AssetNameEquals("Buildings/TractorGarage")
                || asset.AssetNameEquals($"{this.PublicAssetBasePath}/Tractor")
                || asset.AssetNameEquals($"{this.PublicAssetBasePath}/Garage")
                || asset.AssetNameEquals($"{this.PublicAssetBasePath}/BuffIcon");
        }

        /// <inheritdoc />
        public T Load<T>(IAssetInfo asset)
        {
            // Allow for garages from older versions that didn't get normalized correctly.
            // This can be removed once support for legacy data is dropped.
            if (asset.AssetNameEquals($"Buildings/TractorGarage"))
                return (T)(object)this.GarageTexture;

            // load tractor, garage, or buff texture
            string key = PathUtilities.GetSegments(asset.AssetName).Last();
            return this.TryLoadFromFile(key, out Texture2D texture, out string error)
                ? (T)(object)texture
                : throw new InvalidOperationException(error);
        }

        /// <inheritdoc />
        public bool CanEdit<T>(IAssetInfo asset)
        {
            return asset.AssetNameEquals("Data/BuildingsData");
        }

        /// <inheritdoc />
        public void Edit<T>(IAssetData asset)
        {
            var data = asset.AsDictionary<string, BuildingData>().Data;

            data[this.GarageBuildingId] = new BuildingData
            {
                ID = this.GarageBuildingId,
                Name = I18n.Garage_Name(),
                Description = I18n.Garage_Description(),
                Texture = $"{this.PublicAssetBasePath}/Garage",
                BuildingType = typeof(Stable).FullName,
                SortTileOffset = 1,

                Builder = "Carpenter",
                BuildCost = this.Config.BuildPrice,
                BuildMaterials = this.Config.BuildMaterials
                    .Select(p => new BuildingMaterial
                    {
                        ItemID = p.Key,
                        Amount = p.Value
                    })
                    .ToList(),
                BuildDays = 2,

                Size = new Point(4, 2),
                CollisionMap = "XXXX\nXOOX"
            };
        }


        /*********
        ** Private methods
        *********/
        /****
        ** Event handlers
        ****/
        /// <summary>The event called after the first game update, once all mods are loaded.</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event arguments.</param>
        private void OnGameLaunched(object sender, GameLaunchedEventArgs e)
        {
            // add Generic Mod Config Menu integration
            new GenericModConfigMenuIntegrationForTractor(
                getConfig: () => this.Config,
                reset: () =>
                {
                    this.Config = new ModConfig();
                    this.Helper.WriteConfig(this.Config);
                    this.UpdateConfig();
                },
                saveAndApply: () =>
                {
                    this.Helper.WriteConfig(this.Config);
                    this.UpdateConfig();
                },
                modRegistry: this.Helper.ModRegistry,
                monitor: this.Monitor,
                manifest: this.ModManifest
            ).Register();
        }

        /// <summary>The event called after a save slot is loaded.</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event arguments.</param>
        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            // load legacy data
            Migrator.AfterLoad(this.Helper, this.Monitor, this.ModManifest.Version);

            // check if mod should be enabled for the current player
            this.IsEnabled = Context.IsMainPlayer;
            if (!this.IsEnabled)
            {
                ISemanticVersion hostVersion = this.Helper.Multiplayer.GetConnectedPlayer(Game1.MasterPlayer.UniqueMultiplayerID)?.GetMod(this.ModManifest.UniqueID)?.Version;
                if (hostVersion == null)
                {
                    this.IsEnabled = false;
                    this.Monitor.Log("This mod is disabled because the host player doesn't have it installed.", LogLevel.Warn);
                }
                else if (hostVersion.IsOlderThan(this.MinHostVersion))
                {
                    this.IsEnabled = false;
                    this.Monitor.Log($"This mod is disabled because the host player has {this.ModManifest.Name} {hostVersion}, but the minimum compatible version is {this.MinHostVersion}.", LogLevel.Warn);
                }
                else
                    this.IsEnabled = true;
            }
        }

        /// <summary>The event called when a new day begins.</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event arguments.</param>
        private void OnDayStarted(object sender, DayStartedEventArgs e)
        {
            if (!this.IsEnabled)
                return;

            // reload textures
            if (!this.TryLoadFromContent("tractor", out this.TractorTexture, out string error))
                this.Monitor.Log(error, LogLevel.Error);
            if (!this.TryLoadFromContent("garage", out this.GarageTexture, out error))
                this.Monitor.Log(error, LogLevel.Error);
            if (!this.TryLoadFromContent("buffIcon", out this.BuffIconTexture, out error))
                this.Monitor.Log(error, LogLevel.Error);

            // init garages + tractors
            if (Context.IsMainPlayer)
            {
                foreach (GameLocation location in this.GetLocations())
                {
                    foreach (Stable garage in this.GetGaragesIn(location))
                    {
                        // spawn new tractor if needed
                        Horse tractor = this.FindHorse(garage.HorseId);
                        if (!garage.isUnderConstruction())
                        {
                            Vector2 tractorTile = this.GetDefaultTractorTile(garage);
                            if (tractor == null)
                            {
                                tractor = new Horse(garage.HorseId, (int)tractorTile.X, (int)tractorTile.Y);
                                location.addCharacter(tractor);
                            }
                            tractor.DefaultPosition = tractorTile;
                        }

                        // normalize tractor
                        if (tractor != null)
                            TractorManager.SetTractorInfo(tractor);

                        // normalize ownership
                        garage.owner.Value = 0;
                        if (tractor != null)
                            tractor.ownerId.Value = 0;

                        // apply textures
                        this.ApplyTextures(tractor);
                    }
                }
            }
        }

        /// <summary>The event called after the location list changes.</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event arguments.</param>
        private void OnLocationListChanged(object sender, LocationListChangedEventArgs e)
        {
            if (!this.IsEnabled)
                return;

            // rescue lost tractors
            if (Context.IsMainPlayer)
            {
                foreach (GameLocation location in e.Removed)
                {
                    foreach (Horse tractor in this.GetTractorsIn(location).ToArray())
                        this.DismissTractor(tractor);
                }
            }
        }

        /// <summary>The event called after the list of NPCs in a location changes.</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event arguments.</param>
        private void OnNpcListChanged(object sender, NpcListChangedEventArgs e)
        {
            if (!this.IsEnabled)
                return;

            // workaround for instantly-built tractors spawning a horse
            if (Context.IsMainPlayer && e.Location.buildings.Any())
            {
                Horse[] horses = e.Added.OfType<Horse>().ToArray();
                if (horses.Any())
                {
                    HashSet<Guid> tractorIDs = new HashSet<Guid>(this.GetGaragesIn(e.Location).Select(p => p.HorseId));
                    foreach (Horse horse in horses)
                    {
                        if (tractorIDs.Contains(horse.HorseId) && !TractorManager.IsTractor(horse))
                            TractorManager.SetTractorInfo(horse);
                    }
                }
            }
        }

        /// <summary>The event called after the player warps into a new location.</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event arguments.</param>
        private void OnWarped(object sender, WarpedEventArgs e)
        {
            if (!e.IsLocalPlayer || !this.TractorManager.IsCurrentPlayerRiding)
                return;

            // fix: warping onto a magic warp while mounted causes an infinite warp loop
            Vector2 tile = CommonHelper.GetPlayerTile(Game1.player);
            string touchAction = Game1.player.currentLocation.doesTileHaveProperty((int)tile.X, (int)tile.Y, "TouchAction", "Back");
            if (this.TractorManager.IsCurrentPlayerRiding && touchAction?.Split(' ', 2).First() is "MagicWarp" or "Warp")
                Game1.currentLocation.lastTouchActionLocation = tile;

            // fix: warping into an event may break the event (e.g. Mr Qi's event on mine level event for the 'Cryptic Note' quest)
            if (Game1.CurrentEvent != null)
                Game1.player.mount.dismount();
        }

        /// <summary>The event called when the game updates (roughly sixty times per second).</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event arguments.</param>
        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            if (!this.IsEnabled)
                return;

            // multiplayer: override textures in the current location
            if (Context.IsWorldReady && Game1.currentLocation != null)
            {
                uint updateRate = Game1.currentLocation.farmers.Count > 1 ? this.TextureUpdateRateWithMultiplePlayers : this.TextureUpdateRateWithSinglePlayer;
                if (e.IsMultipleOf(updateRate))
                {
                    foreach (Horse horse in this.GetTractorsIn(Game1.currentLocation))
                        this.ApplyTextures(horse);
                }
            }

            // update tractor effects
            if (Context.IsPlayerFree)
                this.TractorManager?.Update();
        }

        /// <summary>The event called before the day ends.</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event arguments.</param>
        private void OnDayEnding(object sender, DayEndingEventArgs e)
        {
            if (!this.IsEnabled)
                return;

            if (Context.IsMainPlayer)
            {
                // collect valid stable IDs
                HashSet<Guid> validStableIDs = new HashSet<Guid>(
                    from location in this.GetLocations()
                    from garage in this.GetGaragesIn(location)
                    select garage.HorseId
                );

                // get locations reachable by Utility.findHorse
                HashSet<GameLocation> vanillaLocations = new HashSet<GameLocation>(Game1.locations, new ObjectReferenceComparer<GameLocation>());

                // clean up
                foreach (GameLocation location in this.GetLocations())
                {
                    bool isValidLocation = vanillaLocations.Contains(location);

                    foreach (Horse tractor in this.GetTractorsIn(location).ToArray())
                    {
                        // remove invalid tractor (e.g. building demolished)
                        if (!validStableIDs.Contains(tractor.HorseId))
                        {
                            location.characters.Remove(tractor);
                            continue;
                        }

                        // move tractor out of location that Utility.findHorse can't find
                        if (!isValidLocation)
                            Game1.warpCharacter(tractor, "Farm", new Point(0, 0));
                    }
                }
            }
        }

        /// <summary>The event called after the game saves.</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event arguments.</param>
        private void OnSaved(object sender, SavedEventArgs e)
        {
            Migrator.AfterSave();
        }

        /// <summary>The event called after the game draws the world to the screen.</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event arguments.</param>
        private void OnRenderedWorld(object sender, RenderedWorldEventArgs e)
        {
            if (!this.IsEnabled)
                return;

            // render debug radius
            if (this.Config.HighlightRadius && Context.IsWorldReady && Game1.activeClickableMenu == null && this.TractorManager?.IsCurrentPlayerRiding == true)
                this.TractorManager.DrawRadius(Game1.spriteBatch);
        }

        /// <summary>Raised after the player presses any buttons on the keyboard, controller, or mouse.</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event data.</param>
        private void OnButtonsChanged(object sender, ButtonsChangedEventArgs e)
        {
            if (!this.IsEnabled || !Context.IsPlayerFree)
                return;

            if (this.Keys.SummonTractor.JustPressed() && !Game1.player.isRidingHorse())
                this.SummonTractor();
            else if (this.Keys.DismissTractor.JustPressed() && Game1.player.isRidingHorse())
                this.DismissTractor(Game1.player.mount);
        }

        /// <summary>Raised after a mod message is received over the network.</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event arguments.</param>
        private void OnModMessageReceived(object sender, ModMessageReceivedEventArgs e)
        {
            // tractor request from a farmhand
            if (e.Type == this.RequestTractorMessageID && Context.IsMainPlayer && e.FromModID == this.ModManifest.UniqueID)
            {
                Farmer player = Game1.getFarmer(e.FromPlayerID);
                if (player is { IsMainPlayer: false })
                {
                    this.Monitor.Log(this.SummonLocalTractorTo(player)
                        ? $"Summon tractor for {player.Name} ({e.FromPlayerID})."
                        : $"Received tractor request for {player.Name} ({e.FromPlayerID}), but no tractor is available."
                    );
                }
                else
                    this.Monitor.Log($"Received tractor request for {e.FromPlayerID}, but no such player was found.");
            }
        }

        /// <summary>Raised when the content language changes.</summary>
        /// <param name="code">The new content language.</param>
        private void OnLanguageChange(LocalizedContentManager.LanguageCode code)
        {
            this.Helper.Content.InvalidateCache("Data/BuildingsData");
        }

        /****
        ** Helper methods
        ****/
        /// <summary>Reapply the mod configuration.</summary>
        private void UpdateConfig()
        {
            this.Helper.Content.InvalidateCache("Data/BuildingsData");

            foreach (var pair in this.TractorManagerImpl.GetActiveValues())
                this.UpdateConfigFor(pair.Value);
        }

        /// <summary>Apply the mod configuration to a tractor manager instance.</summary>
        /// <param name="manager">The tractor manager to update.</param>
        private void UpdateConfigFor(TractorManager manager)
        {
            var modRegistry = this.Helper.ModRegistry;
            var reflection = this.Helper.Reflection;
            var toolConfig = this.Config.StandardAttachments;

            manager.UpdateConfig(this.Config, this.Keys, new IAttachment[]
            {
                new CustomAttachment(this.Config.CustomAttachments, modRegistry, reflection), // should be first so it can override default attachments
                new AxeAttachment(toolConfig.Axe, modRegistry, reflection),
                new FertilizerAttachment(toolConfig.Fertilizer, modRegistry, reflection),
                new GrassStarterAttachment(toolConfig.GrassStarter, modRegistry, reflection),
                new HoeAttachment(toolConfig.Hoe, modRegistry, reflection),
                new MeleeBluntAttachment(toolConfig.MeleeBlunt, modRegistry, reflection),
                new MeleeDaggerAttachment(toolConfig.MeleeDagger, modRegistry, reflection),
                new MeleeSwordAttachment(toolConfig.MeleeSword, modRegistry, reflection),
                new MilkPailAttachment(toolConfig.MilkPail, modRegistry, reflection),
                new PickaxeAttachment(toolConfig.PickAxe, modRegistry, reflection),
                new ScytheAttachment(toolConfig.Scythe, modRegistry, reflection),
                new SeedAttachment(toolConfig.Seeds, modRegistry, reflection),
                modRegistry.IsLoaded(SeedBagAttachment.ModId) ? new SeedBagAttachment(toolConfig.SeedBagMod, modRegistry, reflection) : null,
                new ShearsAttachment(toolConfig.Shears, modRegistry, reflection),
                new SlingshotAttachment(toolConfig.Slingshot, modRegistry, reflection),
                new WateringCanAttachment(toolConfig.WateringCan, modRegistry, reflection)
            });
        }

        /// <summary>Initialize the <see cref="AssetMap"/> from the files on disk.</summary>
        private void InitAssetMap()
        {
            // get assets folder
            DirectoryInfo dir = new DirectoryInfo(Path.Combine(this.Helper.DirectoryPath, "assets"));
            if (!dir.Exists)
            {
                this.Monitor.Log("Tractor Mod's 'assets' folder is missing. The mod will not work correctly. Try reinstalling the mod to fix this.", LogLevel.Error);
                return;
            }

            // create map
            foreach (FileInfo file in dir.GetFiles())
                this.AssetMap[file.Name] = file.Name;
        }

        /// <summary>Summon an unused tractor to the player's current position, if any are available.</summary>
        private void SummonTractor()
        {
            bool summoned = this.SummonLocalTractorTo(Game1.player);
            if (!summoned && !Context.IsMainPlayer)
            {
                this.Monitor.Log("Sending tractor request to host player.");
                this.Helper.Multiplayer.SendMessage(
                    message: true,
                    messageType: this.RequestTractorMessageID,
                    modIDs: new[] { this.ModManifest.UniqueID },
                    playerIDs: new[] { Game1.MasterPlayer.UniqueMultiplayerID }
                );
            }
        }

        /// <summary>Summon an unused tractor to a player's current position, if any are available. If the player is a farmhand in multiplayer, only tractors in synced locations can be found by this method.</summary>
        /// <param name="player">The target player.</param>
        /// <returns>Returns whether a tractor was successfully summoned.</returns>
        private bool SummonLocalTractorTo(Farmer player)
        {
            // get player info
            if (player == null)
                return false;
            GameLocation location = player.currentLocation;
            Vector2 tile = player.getTileLocation();

            // find nearest tractor in player's current location (if available), else any location
            Horse tractor = this
                .GetTractorsIn(location, includeMounted: false)
                .OrderBy(match => Utility.distance(tile.X, tile.Y, match.getTileX(), match.getTileY()))
                .FirstOrDefault();
            if (tractor == null)
            {
                tractor = this
                    .GetLocations()
                    .SelectMany(loc => this.GetTractorsIn(loc, includeMounted: false))
                    .FirstOrDefault();
            }

            // create a tractor if needed
            if (tractor == null && this.Config.CanSummonWithoutGarage && Context.IsMainPlayer)
            {
                tractor = new Horse(Guid.NewGuid(), 0, 0);
                TractorManager.SetTractorInfo(tractor);
                this.ApplyTextures(tractor);
            }

            // warp to player
            if (tractor != null)
            {
                TractorManager.SetLocation(tractor, location, tile);
                return true;
            }
            return false;
        }

        /// <summary>Send a tractor back home.</summary>
        /// <param name="tractor">The tractor to dismiss.</param>
        private void DismissTractor(Horse tractor)
        {
            if (tractor == null || !this.IsTractor(tractor))
                return;

            // dismount
            if (tractor.rider != null)
                tractor.dismount();

            // get home position (garage may have been moved since the tractor was spawned)
            Farm location = Game1.getFarm();
            Stable garage = location.buildings.OfType<Stable>().FirstOrDefault(p => p.HorseId == tractor.HorseId);
            Vector2 tile = garage != null
                ? this.GetDefaultTractorTile(garage)
                : tractor.DefaultPosition;

            // warp home
            TractorManager.SetLocation(tractor, location, tile);
        }

        /// <summary>Get all available locations.</summary>
        private IEnumerable<GameLocation> GetLocations()
        {
            GameLocation[] mainLocations = (Context.IsMainPlayer ? Game1.locations : this.Helper.Multiplayer.GetActiveLocations()).ToArray();

            foreach (GameLocation location in mainLocations.Concat(MineShaft.activeMines).Concat(VolcanoDungeon.activeLevels))
            {
                yield return location;

                foreach (Building building in location.buildings)
                {
                    if (building.indoors.Value != null)
                        yield return building.indoors.Value;
                }
            }
        }

        /// <summary>Get all tractors in the given location.</summary>
        /// <param name="location">The location to scan.</param>
        /// <param name="includeMounted">Whether to include horses that are currently being ridden.</param>
        private IEnumerable<Horse> GetTractorsIn(GameLocation location, bool includeMounted = true)
        {
            // single-player
            if (!Context.IsMultiplayer || !includeMounted)
                return location.characters.OfType<Horse>().Where(this.IsTractor);

            // multiplayer
            return
                location.characters.OfType<Horse>().Where(this.IsTractor)
                    .Concat(
                        from player in location.farmers
                        where this.IsTractor(player.mount)
                        select player.mount
                    )
                    .Distinct(new ObjectReferenceComparer<Horse>());
        }

        /// <summary>Get all tractor garages in the given location.</summary>
        /// <param name="location">The location to scan.</param>
        private IEnumerable<Stable> GetGaragesIn(GameLocation location)
        {
            return location.buildings
                .OfType<Stable>()
                .Where(this.IsGarage);
        }

        /// <summary>Find all horses with a given ID.</summary>
        /// <param name="id">The unique horse ID.</param>
        private Horse FindHorse(Guid id)
        {
            foreach (GameLocation location in this.GetLocations())
            {
                foreach (Horse horse in location.characters.OfType<Horse>())
                {
                    if (horse.HorseId == id)
                        return horse;
                }
            }

            return null;
        }

        /// <summary>Get whether a stable is a tractor garage.</summary>
        /// <param name="stable">The stable to check.</param>
        private bool IsGarage(Stable stable)
        {
            return stable?.buildingData?.ID == this.GarageBuildingId;
        }

        /// <summary>Get whether a horse is a tractor.</summary>
        /// <param name="horse">The horse to check.</param>
        private bool IsTractor(Horse horse)
        {
            return TractorManager.IsTractor(horse);
        }

        /// <summary>Get the default tractor tile position in a garage.</summary>
        /// <param name="garage">The tractor's home garage.</param>
        private Vector2 GetDefaultTractorTile(Stable garage)
        {
            return new Vector2(garage.tileX.Value + 1, garage.tileY.Value + 1);
        }

        /// <summary>Try to load the asset for a texture from the game's content folder so other mods can apply edits.</summary>
        /// <param name="spritesheet">The spritesheet name without the path or extension (like 'Tractor' or 'Garage').</param>
        /// <param name="texture">The loaded texture, if found.</param>
        /// <param name="error">A human-readable error to show to the user if texture wasn't found.</param>
        private bool TryLoadFromContent(string spritesheet, out Texture2D texture, out string error)
        {
            try
            {
                texture = Game1.content.Load<Texture2D>($"{this.PublicAssetBasePath}/{spritesheet}");
                error = null;
            }
            catch (Exception ex)
            {
                texture = null;
                error = ex.ToString();
                return false;
            }

            return texture != null;
        }

        /// <summary>Try to load the asset for a texture from the assets folder (including seasonal logic if applicable).</summary>
        /// <param name="spritesheet">The spritesheet name without the path or extension (like 'tractor' or 'garage').</param>
        /// <param name="texture">The loaded texture, if found.</param>
        /// <param name="error">A human-readable error to show to the user if texture wasn't found.</param>
        private bool TryLoadFromFile(string spritesheet, out Texture2D texture, out string error)
        {
            texture = this.TryGetTextureKey(spritesheet, out string key, out error)
                ? this.Helper.Content.Load<Texture2D>(key)
                : null;

            return texture != null;
        }

        /// <summary>Try to get the asset key for a texture from the assets folder (including seasonal logic if applicable).</summary>
        /// <param name="spritesheet">The spritesheet name without the path or extension (like 'tractor' or 'garage').</param>
        /// <param name="key">The asset key to use, if found.</param>
        /// <param name="error">A human-readable error to show to the user if texture wasn't found.</param>
        private bool TryGetTextureKey(string spritesheet, out string key, out string error)
        {
            string seasonalKey = $"{Game1.currentSeason}_{spritesheet}.png";
            string defaultKey = $"{spritesheet}.png";

            foreach (string possibleKey in new[] { seasonalKey, defaultKey })
            {
                if (this.AssetMap.TryGetValue(possibleKey, out string actualKey) && File.Exists(Path.Combine(this.Helper.DirectoryPath, $"assets/{actualKey}")))
                {
                    key = $"assets/{actualKey}";
                    error = null;
                    return true;
                }
            }

            key = null;
            error = $"Couldn't find file '{defaultKey}' in the mod folder. This mod isn't installed correctly; try reinstalling the mod to fix it.";
            return false;
        }

        /// <summary>Apply the mod textures to the given stable, if applicable.</summary>
        /// <param name="horse">The horse to change.</param>
        private void ApplyTextures(Horse horse)
        {
            if (this.TractorTexture != null && this.IsTractor(horse))
                horse.Sprite.spriteTexture = this.TractorTexture;
        }
    }
}
