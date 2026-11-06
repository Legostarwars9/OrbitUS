using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Orbit_Us
{
    // The host owns celestial simulation. Clients retain the game's gravity sources,
    // colliders and interaction components, but never simulate a second universe.
    public sealed class PlanetReplicator
    {
        private sealed class Replica
        {
            public GameObject Object;
            public Rigidbody2D Body;
            public PlanetState State;
            public float ReceivedAt;
        }

        private static readonly Dictionary<GameObject, Dictionary<Transform, string>> stablePaths =
            new Dictionary<GameObject, Dictionary<Transform, string>>();
        private static readonly Dictionary<GameObject, GameObject> origins = new Dictionary<GameObject, GameObject>();
        private readonly NetworkUDPConnection connection;
        private readonly bool isHost;
        private readonly Dictionary<int, GameObject> prefabs = new Dictionary<int, GameObject>();
        private readonly Dictionary<gravity, int> hostIds = new Dictionary<gravity, int>();
        private readonly Dictionary<int, Replica> replicas = new Dictionary<int, Replica>();
        private readonly Dictionary<string, Sprite> sprites = new Dictionary<string, Sprite>(StringComparer.Ordinal);
        private readonly Dictionary<MapProceeding, bool> stoppedMaps = new Dictionary<MapProceeding, bool>();
        private readonly HashSet<string> warnings = new HashSet<string>();
        private readonly PlanetSnapshotCodec.Assembler assembler = new PlanetSnapshotCodec.Assembler();
        private GameObject staging;
        private PlanetState originalCenter;
        private GameObject originalCenterObject;
        private RigidbodyType2D originalCenterBodyType;
        private bool originalKickAutomatic, originalKickKicked, originalKickEnabled;
        private bool originalCenterHadRocheLimiter;
        private static readonly FieldInfo nearestStarDistance = AccessTools.Field(typeof(MapProceeding), "nearestStarDistance");
        private static readonly FieldInfo nearestStarDistanceSqr = AccessTools.Field(typeof(MapProceeding), "nearestStarDistanceSqr");
        private Vector3 originalCenterScale;
        private List<PlanetState> pendingSnapshot;
        private const float MaxPredictionSeconds = 1f;
        private gravity center;
        private spawner source;
        private bool initialized;
        private bool sending;
        private int nextId = 1;
        private int nextSnapshot = 1;
        private float timer;
        private float scanTimer;
        private float interval = 0.5f;
        public static PlanetReplicator Instance { get; private set; }

        public PlanetReplicator(NetworkUDPConnection connection, bool isHost)
        {
            this.connection = connection;
            this.isHost = isHost;
        }

        public static void RegisterSpawn(GameObject prefab, GameObject instance)
        {
            if (prefab == null || instance == null || instance.GetComponent<gravity>() == null) return;
            stablePaths.TryGetValue(prefab, out Dictionary<Transform, string> inherited);
            stablePaths[instance] = BuildPaths(prefab.transform, instance.transform, inherited);
            if (origins.TryGetValue(prefab, out GameObject original)) prefab = original;
            origins[instance] = prefab;
        }

        public static bool IsReplica(Component component)
        {
            var instance = Instance;
            if (instance == null || !instance.initialized || instance.isHost || component == null) return false;
            Transform current = component.transform;
            while (current != null)
            {
                if (current.GetComponent<PlanetReplicaMarker>() != null) return true;
                current = current.parent;
            }
            return false;
        }

        public void Initialize()
        {
            Instance = this;
            initialized = true;
            connection.OnPacketReceived += HandlePacket;
            RefreshCatalog();
            if (!isHost) StopLocalGeneration();
        }

        private void RefreshCatalog()
        {
            source = UnityEngine.Object.FindObjectOfType<spawner>();
            if (source == null) return;
            if (originalCenterObject == null && originalCenter != null)
            {
                originalCenter = null;
                originalCenterObject = null;
            }
            prefabs.Clear();
            center = source.blackHole != null ? source.blackHole : gameManager.Instance?.centerBlackHole;
            prefabs[1] = source.body;
            prefabs[2] = source.bodyStar;
            prefabs[3] = source.livelyPlanet;
            prefabs[4] = source.dataHubPrefab;
            prefabs[5] = source.vaultPlanetPrefab;
            prefabs[6] = source.materiaphagePlanet;
            prefabs[7] = source.radarPlanet;
            for (int i = 0; i < source.shopPlanetPool.Count; i++) prefabs[100 + i] = source.shopPlanetPool[i];
            foreach (Sprite sprite in Resources.FindObjectsOfTypeAll<Sprite>())
                if (sprite != null && (!sprites.TryGetValue(sprite.name, out Sprite known) || known == null))
                    sprites[sprite.name] = sprite;
            foreach (var key in new List<GameObject>(origins.Keys))
                if (key == null) { origins.Remove(key); stablePaths.Remove(key); }
            foreach (var key in new List<GameObject>(stablePaths.Keys))
                if (key == null) stablePaths.Remove(key);
        }

        private int PrefabKey(gravity body)
        {
            if (body == center) return 1000000;
            if (body.living || body.GetComponent<enemy>() != null || body.GetComponent<player>() != null) return 0;
            if (origins.TryGetValue(body.gameObject, out GameObject original))
                foreach (var pair in prefabs) if (pair.Value == original) return pair.Key;
            if (body.GetComponent<star>() != null || body.GetComponent<blackHole>() != null) return 2;
            // Existing objects from a loaded game can predate the spawn hooks.
            foreach (var pair in prefabs)
            {
                if (pair.Value == null) continue;
                string name = body.name.Replace("(Clone)", "").Trim();
                if (name == pair.Value.name) return pair.Key;
            }
            if (body.GetComponentInChildren<shopField>(true) != null)
            {
                var field = body.GetComponentInChildren<shopField>(true);
                var sr = body.GetComponent<SpriteRenderer>();
                foreach (var pair in prefabs)
                {
                    if (pair.Key < 100 || pair.Value == null) continue;
                    var candidate = pair.Value.GetComponentInChildren<shopField>(true);
                    var candidateSprite = pair.Value.GetComponent<SpriteRenderer>();
                    if (candidate != null && candidate.GetType() == field.GetType() &&
                        sr != null && candidateSprite != null && sr.sprite == candidateSprite.sprite) return pair.Key;
                }
                return 0;
            }
            if (body.GetComponent<livelyPlanet>() != null) return 3;
            // Vaults can also contain a dataHub; prefer their explicit generated name.
            if (body.name.Equals("vault hub", StringComparison.OrdinalIgnoreCase)) return 5;
            if (body.name.Equals("materiaphage planet", StringComparison.OrdinalIgnoreCase)) return 6;
            if (body.GetComponent<dataHub>() != null) return 4;
            if (body.GetComponent<radarPlanet>() != null) return 7;
            switch (body.name.ToLowerInvariant())
            {
                case "planet": case "moon": case "asteroid": case "rogue planet": case "sibling planet": return 1;
                case "vault hub": return 5;
                case "materiaphage planet": return 6;
                case "special planet": case "lively planet": return 3;
            }
            return 0;
        }

        public void Update()
        {
            if (!initialized) return;
            scanTimer += Time.unscaledDeltaTime;
            if (scanTimer >= 1f || source == null)
            {
                scanTimer = 0f;
                RefreshCatalog();
                if (!isHost)
                {
                    StopLocalGeneration();
                    if (pendingSnapshot != null && source != null) TryApplyPendingSnapshot();
                    // Story/cutscene code can generate objects outside MapProceeding.
                    if (replicas.Count > 0) RemoveLocalBodies();
                }
            }
            if (isHost)
            {
                timer += Time.unscaledDeltaTime;
                if (timer >= interval && !sending && source != null && connection.LocalPlayerId > 0)
                {
                    timer = 0f;
                    SendSnapshot();
                }
            }
            else
            {
                foreach (var replica in replicas.Values)
                {
                    if (replica.Object == null || replica.Body == null || replica.State == null) continue;
                    var state = replica.State;
                    float elapsed = Mathf.Max(0f, Time.time - replica.ReceivedAt);
                    float age = Mathf.Min(elapsed, MaxPredictionSeconds);
                    Vector2 position = new Vector2(state.X + state.VelocityX * age, state.Y + state.VelocityY * age);
                    replica.Body.position = Vector2.Lerp(replica.Body.position, position, Mathf.Min(1f, Time.deltaTime * 12f));
                    replica.Body.rotation = Mathf.LerpAngle(replica.Body.rotation, state.Rotation + state.AngularVelocity * age,
                        Mathf.Min(1f, Time.deltaTime * 12f));
                    // Kinematic velocity preserves relative speed for landing and drag.
                    bool predicting = elapsed < MaxPredictionSeconds && state.Active;
                    replica.Body.velocity = predicting ? new Vector2(state.VelocityX, state.VelocityY) : Vector2.zero;
                    replica.Body.angularVelocity = predicting ? state.AngularVelocity : 0f;
                }
                UpdateMapReferences();
            }
        }

        private void StopLocalGeneration()
        {
            foreach (var map in UnityEngine.Object.FindObjectsOfType<MapProceeding>(true))
            {
                if (!stoppedMaps.ContainsKey(map)) stoppedMaps.Add(map, map.enabled);
                map.StopLoadChecking();
                map.enabled = false;
            }
        }

        private async void SendSnapshot()
        {
            sending = true;
            try
            {
                var bodies = new List<gravity>();
                foreach (var body in UnityEngine.Object.FindObjectsOfType<gravity>(true))
                {
                    if (PrefabKey(body) == 0) continue;
                    bodies.Add(body);
                    if (!hostIds.ContainsKey(body)) hostIds.Add(body, nextId++);
                }
                var states = new List<PlanetState>();
                foreach (var body in bodies) states.Add(Capture(body, hostIds[body], PrefabKey(body)));
                foreach (var body in new List<gravity>(hostIds.Keys)) if (body == null) hostIds.Remove(body);
                // Full appearance/stock is sent at 2 Hz, reduced for large worlds.
                // Each visual has strings and color data; estimate conservatively.
                int bytes = 0;
                foreach (var state in states) bytes += 180 + state.Visuals.Count * 180 + state.Shops.Count * 160;
                interval = Mathf.Max(0.5f, bytes / 100000f);
                await connection.SendPlanetSnapshot(states, nextSnapshot++);
            }
            catch (Exception ex) { Debug.LogError($"[Orbit-Us] Planet snapshot failed: {ex}"); }
            finally { sending = false; }
        }

        private PlanetState Capture(gravity body, int id, int prefabKey)
        {
            var t = body.transform;
            if (!stablePaths.ContainsKey(body.gameObject))
            {
                Transform template = prefabs.TryGetValue(prefabKey, out GameObject prefab) && prefab != null
                    ? prefab.transform : t;
                stablePaths[body.gameObject] = BuildPaths(template, t, null);
            }
            var rb = body.GetComponent<Rigidbody2D>();
            var temp = body.GetComponent<temperature>();
            var sun = body.GetComponent<star>();
            var kick = body.GetComponent<kickStart>();
            var lively = body.GetComponent<livelyPlanet>();
            bool moving = body.gameObject.activeInHierarchy && (!isHost || Time.timeScale > 0f);
            var state = new PlanetState
            {
                Id = id, PrefabKey = prefabKey, Name = body.name, Tag = body.tag,
                Active = body.gameObject.activeInHierarchy, CentralBlackHole = body == center,
                IsBlackHole = body.GetComponent<blackHole>() != null,
                MainSequence = sun != null && sun.mainSequence, PostMainSequence = sun != null && sun.postPostMS,
                HasBoss = lively != null && lively.hasBoss,
                X = t.position.x, Y = t.position.y, Rotation = t.eulerAngles.z,
                ScaleX = t.lossyScale.x, ScaleY = t.lossyScale.y, Mass = rb.mass,
                VelocityX = moving ? rb.velocity.x : 0f,
                VelocityY = moving ? rb.velocity.y : 0f,
                AngularVelocity = moving ? rb.angularVelocity : 0f,
                Heat = temp != null ? temp.heat : 0f,
                ParentId = kick != null && kick.parent != null && hostIds.TryGetValue(kick.parent, out int parent) ? parent : 0
            };
            foreach (var sr in body.GetComponentsInChildren<SpriteRenderer>(true))
            {
                string path = PathFor(t, sr.transform);
                if (path == null) continue;
                var atmo = sr.GetComponent<atmosphere>();
                state.Visuals.Add(new PlanetVisualState
                {
                    Path = path, SpriteName = sr.sprite != null ? sr.sprite.name : "",
                    R = sr.color.r, G = sr.color.g, B = sr.color.b, A = sr.color.a,
                    ScaleX = sr.transform.localScale.x, ScaleY = sr.transform.localScale.y,
                    Active = sr.gameObject.activeSelf, Enabled = sr.enabled,
                    AtmosphereDrag = atmo != null ? atmo.dragCof : 0f
                });
            }
            foreach (var field in body.GetComponentsInChildren<shopField>(true))
            {
                string path = PathFor(t, field.transform);
                if (path == null || field.randIDs == null || field.randIDs.Length != 6) continue;
                state.Shops.Add(new PlanetShopState
                {
                    Path = path, ItemIds = (int[])field.randIDs.Clone(), ItemTypes = (int[])field.itemTypes.Clone(),
                    PriceMultipliers = (int[])field.priceMultipliers.Clone()
                });
            }
            return state;
        }

        private static Dictionary<Transform, string> BuildPaths(Transform template, Transform instance,
            Dictionary<Transform, string> inherited)
        {
            var result = new Dictionary<Transform, string> { { instance, "" } };
            MapChildren(template, instance, "", inherited, result);
            return result;
        }

        private static void MapChildren(Transform template, Transform instance, string parentPath,
            Dictionary<Transform, string> inherited, Dictionary<Transform, string> result)
        {
            var used = new HashSet<Transform>();
            for (int i = 0; i < template.childCount; i++)
            {
                Transform original = template.GetChild(i);
                if (original.GetComponent<gravity>() != null) continue;
                Transform clone = i < instance.childCount && instance.GetChild(i).name == original.name
                    ? instance.GetChild(i) : null;
                if (clone == null || used.Contains(clone))
                {
                    clone = null;
                    foreach (Transform candidate in instance)
                        if (!used.Contains(candidate) && candidate.name == original.name) { clone = candidate; break; }
                }
                if (clone == null || clone.GetComponent<gravity>() != null) continue;
                used.Add(clone);
                string path = parentPath.Length == 0 ? i.ToString() : parentPath + "/" + i;
                if (inherited != null && !inherited.TryGetValue(original, out path)) continue;
                result[clone] = path;
                MapChildren(original, clone, path, inherited, result);
            }
        }

        private static string PathFor(Transform root, Transform child)
        {
            if (child == root) return "";
            // Preserve prefab indices even after Destroy shifts sibling indices.
            // Runtime particle attachments are not part of the prefab's visuals.
            return stablePaths.TryGetValue(root.gameObject, out Dictionary<Transform, string> paths) &&
                paths.TryGetValue(child, out string path) ? path : null;
        }

        private static Transform FindPath(Transform root, string path)
        {
            if (path == "") return root;
            foreach (string part in path.Split('/'))
            {
                if (!int.TryParse(part, out int index) || index < 0 || index >= root.childCount) return null;
                root = root.GetChild(index);
            }
            return root;
        }

        private void HandlePacket(NetworkPacket packet)
        {
            if (!initialized || isHost || packet.Type != PacketType.PlanetSnapshot) return;
            if (!assembler.TryAdd(packet.Data, out List<PlanetState> states)) return;
            pendingSnapshot = states;
            RefreshCatalog();
            TryApplyPendingSnapshot();
        }

        private void TryApplyPendingSnapshot()
        {
            if (pendingSnapshot == null || source == null) return;
            var states = pendingSnapshot;
            if (!ValidateReferences(states)) return;
            pendingSnapshot = null;
            RemoveLocalBodies();
            var received = new HashSet<int>();
            foreach (var state in states)
            {
                received.Add(state.Id);
                replicas.TryGetValue(state.Id, out Replica replica);
                if (replica != null && replica.State != null &&
                    (replica.State.PrefabKey != state.PrefabKey || replica.State.CentralBlackHole != state.CentralBlackHole))
                {
                    RemoveReplica(state.Id);
                    replica = null;
                }
                if (replica == null || replica.Object == null)
                {
                    replica = CreateReplica(state);
                    replicas[state.Id] = replica;
                }
                Apply(replica, state);
            }
            foreach (int id in new List<int>(replicas.Keys)) if (!received.Contains(id)) RemoveReplica(id);
            bool receivedCenter = false;
            foreach (var state in states) receivedCenter |= state.CentralBlackHole;
            if (!receivedCenter && center != null)
            {
                if (originalCenter == null) SaveCenter(1, 1000000);
                center.gameObject.SetActive(false);
            }
            foreach (var replica in replicas.Values)
            {
                if (replica.Object == null) continue;
                var kick = replica.Object.GetComponent<kickStart>();
                if (kick != null)
                    kick.parent = replicas.TryGetValue(replica.State.ParentId, out Replica parent) && parent.Object != null
                        ? parent.Object.GetComponent<gravity>() : null;
            }
            UpdateMapReferences();
        }

        private bool ValidateReferences(List<PlanetState> states)
        {
            var ids = new HashSet<int>();
            int centralCount = 0;
            foreach (var state in states) ids.Add(state.Id);
            var localShop = UnityEngine.Object.FindObjectOfType<shop>(true);
            foreach (var state in states)
            {
                GameObject template;
                if (state.CentralBlackHole)
                {
                    if (++centralCount > 1 || state.PrefabKey != 1000000 || center == null)
                        return Reject("Invalid central black hole reference.");
                    template = center.gameObject;
                }
                else if (!prefabs.TryGetValue(state.PrefabKey, out template) || template == null)
                    return Reject("Missing planet prefab " + state.PrefabKey + "; both players need the same game build.");
                if (template.GetComponent<gravity>() == null || template.GetComponent<Rigidbody2D>() == null)
                    return Reject("Invalid planet template " + state.PrefabKey);
                if (state.ParentId != 0 && !ids.Contains(state.ParentId))
                    return Reject("Missing orbital parent " + state.ParentId);
                try { template.CompareTag(state.Tag); }
                catch (UnityException) { return Reject("Unknown planet tag: " + state.Tag); }
                foreach (var visual in state.Visuals)
                {
                    Transform target = FindPath(template.transform, visual.Path);
                    var renderer = target != null ? target.GetComponent<SpriteRenderer>() : null;
                    if (renderer == null) return Reject("Unsupported planet visual path: " + visual.Path);
                    if (visual.SpriteName != "" && (renderer.sprite == null || renderer.sprite.name != visual.SpriteName) &&
                        (!sprites.TryGetValue(visual.SpriteName, out Sprite sprite) || sprite == null))
                        return Reject("Missing planet sprite: " + visual.SpriteName);
                }
                foreach (var stock in state.Shops)
                {
                    Transform target = FindPath(template.transform, stock.Path);
                    if (target == null || target.GetComponent<shopField>() == null || localShop == null)
                        return Reject("Missing local planet shop reference: " + stock.Path);
                    for (int i = 0; i < 6; i++)
                    {
                        int type = stock.ItemTypes[i], id = stock.ItemIds[i];
                        if (localShop.itemPools == null || type >= localShop.itemPools.Length || localShop.itemPools[type] == null)
                            return Reject("Unsupported shop item category.");
                        var pool = localShop.itemPools[type];
                        if (id != -2 && (id < 0 || id >= pool.names.Length || id >= pool.sprites.Length || id >= pool.prices.Length))
                            return Reject("Unsupported shop item ID.");
                    }
                }
            }
            return true;
        }

        private bool Reject(string message)
        {
            Warn(message);
            return false;
        }

        private void SaveCenter(int id, int prefabKey)
        {
            originalCenterObject = center.gameObject;
            originalCenter = Capture(center, id, prefabKey);
            originalCenterBodyType = center.GetComponent<Rigidbody2D>().bodyType;
            originalCenterScale = center.transform.localScale;
            originalCenterHadRocheLimiter = center.GetComponent<rocheLimiter>() != null;
            var kick = center.GetComponent<kickStart>();
            if (kick != null)
            {
                originalKickAutomatic = kick.automatic;
                originalKickKicked = kick.kicked;
                originalKickEnabled = kick.enabled;
            }
        }

        private void RestoreCenter()
        {
            if (originalCenter == null || originalCenterObject == null) return;
            var body = originalCenterObject.GetComponent<Rigidbody2D>();
            Apply(new Replica { Object = originalCenterObject, Body = body }, originalCenter);
            originalCenterObject.transform.localScale = originalCenterScale;
            body.bodyType = originalCenterBodyType;
            body.velocity = new Vector2(originalCenter.VelocityX, originalCenter.VelocityY);
            body.angularVelocity = originalCenter.AngularVelocity;
            var kick = originalCenterObject.GetComponent<kickStart>();
            if (kick != null)
            {
                kick.automatic = originalKickAutomatic;
                kick.kicked = originalKickKicked;
                kick.enabled = originalKickEnabled;
            }
            if (!originalCenterHadRocheLimiter)
            {
                var limiter = originalCenterObject.GetComponent<rocheLimiter>();
                if (limiter != null) UnityEngine.Object.Destroy(limiter);
            }
            var marker = originalCenterObject.GetComponent<PlanetReplicaMarker>();
            if (marker != null) UnityEngine.Object.Destroy(marker);
            originalCenter = null;
            originalCenterObject = null;
        }

        private void RemoveLocalBodies()
        {
            foreach (var body in UnityEngine.Object.FindObjectsOfType<gravity>(true))
            {
                if (body == center || IsReplica(body) || PrefabKey(body) == 0) continue;
                body.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(body.gameObject);
            }
        }

        private Replica CreateReplica(PlanetState state)
        {
            GameObject obj;
            if (state.CentralBlackHole)
            {
                obj = center.gameObject;
                if (originalCenter == null)
                {
                    SaveCenter(state.Id, state.PrefabKey);
                }
            }
            else
            {
                if (staging == null)
                {
                    staging = new GameObject("OrbitUs_PlanetStaging");
                    staging.SetActive(false);
                }
                obj = UnityEngine.Object.Instantiate(prefabs[state.PrefabKey], staging.transform);
                RegisterSpawn(prefabs[state.PrefabKey], obj);
                obj.SetActive(false);
            }
            if (obj.GetComponent<PlanetReplicaMarker>() == null) obj.AddComponent<PlanetReplicaMarker>();
            var rb = obj.GetComponent<Rigidbody2D>();
            rb.bodyType = RigidbodyType2D.Kinematic;
            var kick = obj.GetComponent<kickStart>();
            if (kick != null) { kick.automatic = false; kick.kicked = true; kick.enabled = false; }
            foreach (var behaviour in obj.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour is star || behaviour is temperature || behaviour is atmosphere)
                    behaviour.StopAllCoroutines();
            }
            var result = new Replica { Object = obj, Body = rb };
            Apply(result, state);
            if (!state.CentralBlackHole) obj.transform.SetParent(null, true);
            obj.SetActive(state.Active);
            return result;
        }

        private void Apply(Replica replica, PlanetState state)
        {
            bool first = replica.State == null;
            var obj = replica.Object;
            obj.name = state.Name;
            obj.tag = state.Tag;
            obj.transform.localScale = new Vector3(state.ScaleX, state.ScaleY, 1f);
            replica.Body.mass = state.Mass;
            if (first || (replica.Body.position - new Vector2(state.X, state.Y)).sqrMagnitude > 1000000f)
            {
                replica.Body.position = new Vector2(state.X, state.Y);
                replica.Body.rotation = state.Rotation;
            }
            var temp = obj.GetComponent<temperature>();
            if (temp != null)
            {
                temp.rb = replica.Body; temp.mass = state.Mass; temp.heat = state.Heat;
                temp.gameManager = gameManager.Instance; temp.audManager = audioManager.Instance;
                if (gameManager.Instance != null) temp.gradient = gameManager.Instance.gradient;
                temp.lastHeat = state.Heat;
            }
            var sun = obj.GetComponent<star>();
            if (sun != null)
            {
                sun.mainSequence = state.MainSequence;
                sun.postPostMS = state.PostMainSequence;
                sun.gameManager = gameManager.Instance;
            }
            if (state.IsBlackHole && obj.GetComponent<blackHole>() == null)
                obj.AddComponent<blackHole>().rb = replica.Body;
            if (state.IsBlackHole)
            {
                var limiter = obj.GetComponent<rocheLimiter>();
                if (limiter == null) limiter = obj.AddComponent<rocheLimiter>();
                limiter.rb = replica.Body;
                limiter.gameManager = gameManager.Instance;
            }
            if (sun != null && state.IsBlackHole)
            {
                // BecomeBlackHole disables every child, including lights and
                // particle objects with no SpriteRenderer of their own.
                foreach (Transform child in obj.transform) child.gameObject.SetActive(false);
            }
            var lively = obj.GetComponent<livelyPlanet>();
            if (lively != null) lively.hasBoss = state.HasBoss;
            var visualPaths = new HashSet<string>();
            foreach (var visual in state.Visuals)
            {
                visualPaths.Add(visual.Path);
                Transform child = FindPath(obj.transform, visual.Path);
                if (child == null) continue;
                var sr = child.GetComponent<SpriteRenderer>();
                if (sr == null) continue;
                sr.color = new Color(visual.R, visual.G, visual.B, visual.A);
                if (child == obj.transform && temp != null) temp.identityColor = sr.color;
                sr.enabled = visual.Enabled;
                if (visual.SpriteName == "") sr.sprite = null;
                else if (sr.sprite != null && sr.sprite.name == visual.SpriteName) { }
                else if (sprites.TryGetValue(visual.SpriteName, out Sprite sprite)) sr.sprite = sprite;
                else Warn("Missing planet sprite: " + visual.SpriteName);
                if (child != obj.transform)
                {
                    child.localScale = new Vector3(visual.ScaleX, visual.ScaleY, child.localScale.z);
                    child.gameObject.SetActive(visual.Active);
                }
                var atmo = child.GetComponent<atmosphere>();
                if (atmo != null) { atmo.dragCof = visual.AtmosphereDrag; atmo.planetRb = replica.Body; }
            }
            // Removed/burned atmosphere and vegetation must not reappear locally.
            foreach (var sr in obj.GetComponentsInChildren<SpriteRenderer>(true))
            {
                string path = PathFor(obj.transform, sr.transform);
                if (path != null && path != "" && !visualPaths.Contains(path)) sr.gameObject.SetActive(false);
            }
            foreach (var stock in state.Shops)
            {
                Transform child = FindPath(obj.transform, stock.Path);
                var field = child != null ? child.GetComponent<shopField>() : null;
                if (field == null) continue;
                field.shop = UnityEngine.Object.FindObjectOfType<shop>(true);
                field.randIDs = (int[])stock.ItemIds.Clone();
                field.itemTypes = (int[])stock.ItemTypes.Clone();
                if (field.priceMultipliers == null || field.priceMultipliers.Length != 6)
                    field.priceMultipliers = new int[6];
                for (int i = 0; i < 6; i++)
                    field.priceMultipliers[i] = first ? stock.PriceMultipliers[i] : Math.Max(field.priceMultipliers[i], stock.PriceMultipliers[i]);
                if (field.shop != null && field.shop.currentShopField == field) field.ChangeShop();
            }
            replica.State = state;
            replica.ReceivedAt = Time.time;
            obj.SetActive(state.Active);
            // star.FixedUpdate also drives its light, so reproduce that visual only.
            if (sun != null && sun.light != null && sun.sr != null)
            {
                sun.light.enabled = !state.IsBlackHole;
                sun.light.color = sun.sr.color;
                sun.light.pointLightOuterRadius = Mathf.Clamp(state.ScaleX * state.Mass / 200f, 0f, 800f);
            }
        }

        private void UpdateMapReferences()
        {
            foreach (var map in stoppedMaps.Keys)
            {
                if (map == null) continue;
                map.allStars.Clear(); map.nearbyStars.Clear(); map.allRogues.Clear(); map.nearbyRogues.Clear();
                map.nearestStar = null;
                map.bossPlanet = null;
                float nearest = float.MaxValue;
                foreach (var replica in replicas.Values)
                {
                    if (replica.Object == null) continue;
                    if (replica.State.HasBoss) map.bossPlanet = replica.Object;
                    if (replica.State.Tag == "RoguePlanet")
                    {
                        map.allRogues.Add(replica.Object);
                        if (replica.Object.activeInHierarchy) map.nearbyRogues.Add(replica.Object);
                    }
                    var sun = replica.Object.GetComponent<star>();
                    if (sun == null || replica.State.IsBlackHole) continue;
                    map.allStars.Add(sun);
                    if (!replica.Object.activeInHierarchy) continue;
                    map.nearbyStars.Add(sun);
                    float distance = (sun.transform.position - map.transform.position).sqrMagnitude;
                    if (distance < nearest) { nearest = distance; map.nearestStar = sun; }
                }
                nearestStarDistance?.SetValue(map, Mathf.Sqrt(nearest));
                nearestStarDistanceSqr?.SetValue(map, nearest);
            }
        }

        private void Warn(string message)
        {
            if (warnings.Add(message)) Debug.LogWarning("[Orbit-Us] " + message);
        }

        private void RemoveReplica(int id)
        {
            var replica = replicas[id];
            if (replica.Object != null && replica.State != null && !replica.State.CentralBlackHole)
            {
                replica.Object.SetActive(false);
                UnityEngine.Object.Destroy(replica.Object);
            }
            replicas.Remove(id);
        }

        public void Destroy()
        {
            connection.OnPacketReceived -= HandlePacket;
            RestoreCenter();
            pendingSnapshot = null;
            initialized = false;
            foreach (int id in new List<int>(replicas.Keys)) RemoveReplica(id);
            foreach (var pair in stoppedMaps)
                if (pair.Key != null)
                {
                    pair.Key.allStars.Clear(); pair.Key.nearbyStars.Clear();
                    pair.Key.allRogues.Clear(); pair.Key.nearbyRogues.Clear(); pair.Key.nearestStar = null;
                    pair.Key.enabled = pair.Value;
                    if (pair.Value && pair.Key.gameObject.activeInHierarchy) pair.Key.StartCoroutine("LoadCheck");
                }
            stoppedMaps.Clear();
            if (staging != null) UnityEngine.Object.Destroy(staging);
            if (Instance == this) Instance = null;
        }
    }

    public sealed class PlanetReplicaMarker : MonoBehaviour { }
}
