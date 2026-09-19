using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using Durango.Network;
using Durango.Online;
using Messages;

const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
static void Set(object target, string field, object value) =>
    target.GetType().GetField(field, Private)!.SetValue(target, value);
static void Call(object target, string method, params object[] args) =>
    target.GetType().GetMethod(method, Private)!.Invoke(target, args);
static void Expect(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS " + message);
}

Durango.Utils.Json.DataDir = Path.GetFullPath(args[0]);
TerrainLoader.TerrainDir = Path.Combine(Durango.Utils.Json.DataDir, "terrains");
string terrainId = Path.GetFileNameWithoutExtension(Directory.GetFiles(TerrainLoader.TerrainDir, "*.zip")[0]);
var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
using var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
clientSocket.Connect(listener.LocalEndpoint);
using var serverSocket = listener.AcceptSocket();
listener.Stop();
var receiving = new Connection(clientSocket);
var sending = new Connection(serverSocket);
receiving.StartReceive();
try
{
    // Exercise the real handlers and wire encoding without booting a saved world.
    var context = new PlayerContext
    {
        RegionId = "personal_regression",
        PersonalRegionId = "personal_regression",
        PersonalRegionTemplateId = terrainId
    };
    var worldContext = new WorldContext
    {
        TerrainId = terrainId,
        Persistent = true, // Do not write a world save during these tests.
        GrazedPetList = new List<Pet>()
    };
    var world = (World)RuntimeHelpers.GetUninitializedObject(typeof(World));
    Set(world, "_context", worldContext);
    Set(world, "_terrainData", new TerrainData { Width = 32, Height = 48 });
    Set(world, "_players", new List<Player>());
    Set(world, "<NumChunksX>k__BackingField", 2);
    Set(world, "<NumChunksY>k__BackingField", 3);
    var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
    Set(player, "_context", context);
    Set(player, "_world", world);
    Set(player, "_connection", sending);
    Set(player, "<EntityId>k__BackingField", "regression-pets");

    RegionMapInfo map = default;
    uint reply = 0;
    receiving.Recv(delegate(RegionMapInfo msg, PacketHeader header) { map = msg; reply = header.ReplyOf; });
    void Receive(uint seq)
    {
        var until = DateTime.UtcNow.AddSeconds(3);
        while (reply != seq && DateTime.UtcNow < until)
        {
            sending.Process();
            receiving.Process();
            Thread.Sleep(5);
        }
        Expect(reply == seq, "response preserves request sequence " + seq);
    }

    Call(player, "HandleGetRegionMapInfoMsg", new GetRegionMapInfo { RegionId = context.RegionId }, 41u);
    Receive(41);
    Expect(map.RegionId == context.RegionId && map.TerrainId == terrainId,
        "personal map separates region identity from terrain filename");
    Expect(map.TileCount.x == 32 && map.TileCount.y == 48 && map.DefoggedChunks.Chunks.Length == 6,
        "current personal map retains dimensions and explored chunks");

    context.RegionId = "another-island";
    Call(player, "HandleGetRegionMapInfoMsg", new GetRegionMapInfo { RegionId = context.PersonalRegionId }, 42u);
    Receive(42);
    Expect(map.TerrainId == terrainId && map.TileCount.x > 0 && map.DefoggedChunks.Chunks.Length == 0,
        "remote personal map loads its terrain template without revealing fog");

    context.RegionId = terrainId;
    Call(player, "HandleGetRegionMapInfoMsg", new GetRegionMapInfo { RegionId = terrainId }, 43u);
    Receive(43);
    Expect(map.TerrainId == terrainId && map.DefoggedChunks.Chunks.Length == 6,
        "ordinary current-island maps still work");

    int changes = 0;
    player.ContextChanged += () => { Call(player, "FlushPersistedState"); changes++; };
    var pets = Player.PetStore.Of(player.EntityId);
    pets.Add(new Player.PetStore.Entry { Pet = new Pet { EntityId = "regression-pet" } });
    Call(player, "HandleGrazePetsMsg", new GrazePets { PetIdsToGraze = new[] { "regression-pet" } }, 44u);
    Expect(changes == 1 && context.Pets.Count == 1 && context.Pets[0].Grazing,
        "grazing synchronizes the player save immediately");
    Expect(worldContext.GrazedPetList.Count == 1, "grazing updates the world list");
    Call(player, "HandleGrazePetsMsg", new GrazePets { PetIdsToGraze = Array.Empty<string>() }, 45u);
    Expect(changes == 2 && !context.Pets[0].Grazing && worldContext.GrazedPetList.Count == 0,
        "recalling grazing pets synchronizes both saves");
    Call(player, "HandleGrazePetsMsg", new GrazePets { PetIdsToGraze = Array.Empty<string>() }, 46u);
    Expect(changes == 2, "unchanged grazing selection does not trigger another save");
    pets.Clear();

    WorkbenchTags.AssetsDir = Path.Combine(Durango.Utils.Json.DataDir, "assets");
    var manager = (AnimalManager)RuntimeHelpers.GetUninitializedObject(typeof(AnimalManager));
    Set(manager, "_rng", new Random(7));
    var motionTable = Newtonsoft.Json.Linq.JObject.Parse(File.ReadAllText(
        Path.Combine(WorkbenchTags.AssetsDir, "derived", "animal_motions.json")));
    var verifiedTypes = motionTable.Properties().Where(p => p.Value["run"] != null).ToArray();
    Expect(verifiedTypes.Length == 205, "205 animal types have asset-verified run mappings");
    foreach (var row in verifiedTypes)
    {
        ushort type = ushort.Parse(row.Name);
        var animal = new AnimalManager.Animal
        {
            EntityId = "animation-regression", EntityType = type,
            Position = new WorldPosition(2000, 2000), HomeTile = new Point2(10, 10)
        };
        Move chase = manager.BuildChase(animal, new WorldPosition(2400, 2000), 100, 100);
        if (chase.Movements?[0].MotionName != (string)row.Value["run"])
            throw new Exception("Wrong chase clip for animal " + type);
        ExpectChasePath(chase);
    }
    Expect(true, "all verified animal types use run clips with unchanged chase timing");
    var wolf = new AnimalManager.Animal
    {
        EntityId = "wolf-regression", EntityType = 2020,
        Position = new WorldPosition(2000, 2000), HomeTile = new Point2(10, 10)
    };
    var wolfMotions = AnimalMotions.Of(wolf.EntityType);
    Expect(wolfMotions.Run == "Wolf_Run", "wolf run clip is loaded from the extracted asset table");
    Move wander = (Move)typeof(AnimalManager).GetMethod("BuildWander", Private)!
        .Invoke(manager, new object[] { wolf, 100d })!;
    Expect(wander.Movements[0].MotionName == "Wolf_Walk", "wandering still uses the walk clip");
    string savedRun = wolfMotions.Run;
    try
    {
        wolfMotions.Run = null;
        Move fallback = manager.BuildChase(wolf, new WorldPosition(4000, 4000), 100, 200);
        Expect(fallback.Movements[0].MotionName == "Wolf_Walk", "older tables without run clips retain walking");
        wolfMotions.Run = "";
        fallback = manager.BuildChase(wolf, new WorldPosition(6000, 6000), 100, 300);
        Expect(fallback.Movements[0].MotionName == "Wolf_Walk", "empty run entries safely retain walking");
    }
    finally { wolfMotions.Run = savedRun; }
    var closeAnimal = new AnimalManager.Animal { EntityType = 2020 };
    Expect(manager.BuildChase(closeAnimal, new WorldPosition(10, 0), 100, 100).Movements == null,
        "animals in attack range do not start a run");
}
finally
{
    receiving.Close();
    sending.Close();
}

static void ExpectChasePath(Move move)
{
    var movement = move.Movements[0];
    if (movement.Path[0].Time != 100 || movement.Path[1].Time != 103 ||
        movement.Path[1].Position.x != 2300 || movement.PlaybackRate != 1f ||
        movement.MotionOption != (byte)(MotionOption.LOOPING | MotionOption.ALIGN_TO_PATH))
        throw new Exception("Run selection changed chase path, timing or movement flags");
}
