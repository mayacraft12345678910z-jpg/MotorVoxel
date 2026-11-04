using Godot;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

/// <summary>
/// Nodo raíz del mundo. Gestiona la carga/descarga de chunks con multihilo,
/// materiales compartidos, ruidos y distancias de renderizado.
/// </summary>
public partial class World : Node3D
{
    // ─────────────────────────────────────────────────────────────────────────
    // PERSISTENCIA (NUEVO): conecta este World con el mundo elegido en el menú
    // ─────────────────────────────────────────────────────────────────────────
    // Si World.cs corre suelto (probando la escena sin pasar por el menú),
    // _persistenceEnabled queda en false y todo funciona como antes: seed
    // random y sin guardar nada en disco.
    private bool _persistenceEnabled = false;
    private string _currentWorldName = "";
    private string _worldSaveDir = "";   // ruta de SO ya globalizada a user://saves/<mundo>/chunks/
    private string _playerSavePath = ""; // ruta de SO ya globalizada a user://saves/<mundo>/player.json
    private int _worldSeed = 0;
    private int _worldType = 0; // 0 por defecto, 1 superplano, 2 biomas grandes, 3 biomas pequeños
    private WorldGenerator.FlatSettings _flatSettings = null; // capas/bioma del superplano (Personalizar)
    private string _singleBiome = null; // bioma del mundo "Bioma único" (Personalizar)

    // Caché en memoria de las ediciones de bloques por chunk (formato VIEJO,
    // solo usado para migrar partidas guardadas antes del snapshot completo).
    private readonly Dictionary<Vector2I, Dictionary<Vector3I, float>> _chunkEditsCache = new();

    // NUEVO: chunks cuyo snapshot completo en disco quedó desactualizado por
    // una edición del jugador (romper/poner bloque) y todavía no se volcó a
    // disco. Se vuelca en lote cada SnapshotFlushInterval segundos en vez de
    // en cada click, para no reescribir el archivo entero (terreno + árboles +
    // estructuras) por cada bloque roto durante una sesión de minería rápida.
    private readonly HashSet<Vector2I> _dirtySnapshotChunks = new();
    private float _snapshotFlushTimer = 0.0f;
    private const float SnapshotFlushInterval = 4.0f;

    private float _playerSaveTimer = 0.0f;
    private const float PlayerAutoSaveInterval = 10.0f; // autosave de posición cada 10s
    // ─────────────────────────────────────────────────────────────────────────
    // EXPORTS
    // ─────────────────────────────────────────────────────────────────────────
    [ExportGroup("Configuración de Distancias")]
    [Export] public int RenderDistance { get; set; } = 16;
    [Export] public int SimulationDistance { get; set; } = 4;
    // OPT-2: Solo chunks dentro de este radio proyectan sombra (Render Shadows ~10 ms GPU).
    // Valor sugerido: 50–60 % de RenderDistance. Con RenderDistance=16 → ShadowDistance=9.
    // OPT-GPU: la luz direccional solo dibuja sombras hasta 90 bloques (≈5.6 chunks, ver
    // directional_shadow_max_distance en World.tscn, ahora 70 bloques con UN solo corte en vez de dos);
    // más allá los chunks seguían entrando al pase de sombras sin que se viera nada. 6 chunks cubre esos
    // 70 bloques + margen para los que proyectan sombra hacia dentro.
    [Export] public int ShadowDistance { get; set; } = 6;

    // OPT-PLANTAS: las plantas (pasto, flores, helechos...) solo se dibujan hasta esta fracción de la
    // distancia de renderizado. Es adaptativo: con RenderDistance 16 y 0.65 llegan a ~10 chunks (160
    // bloques), cuando a esa distancia una mata de pasto mide menos de un píxel y la niebla ya empieza a
    // taparlas. Con distancias de render pequeñas (<= 6) se dibujan siempre. Sube a 1.0 para desactivarlo.
    [Export(PropertyHint.Range, "0.3,1,0.05")] public float PlantDistanceRatio { get; set; } = 0.65f;
    [Export] public int PlantDistanceMinChunks { get; set; } = 6;

    private float ComputePlantMaxChunks()
    {
        if (PlantDistanceRatio >= 0.999f || RenderDistance <= PlantDistanceMinChunks)
            return RenderDistance + 2.0f; // todo el rango: sin recorte
        float d = RenderDistance * PlantDistanceRatio;
        return Mathf.Clamp(d, PlantDistanceMinChunks, RenderDistance + 2.0f);
    }

    // OPT-GPU: el glow (bloom) añade varios pases a pantalla completa; en una iGPU cuesta ms y apenas
    // se nota en un juego de bloques. Ponlo en true si quieres recuperarlo.
    [Export] public bool GlowActivo { get; set; } = false;

    [ExportGroup("Recursos")]
    [Export] public PackedScene ChunkScene { get; set; }
    [Export] public CharacterBody3D Player { get; set; }
    [Export] public Shader WaterShader { get; set; }
    [Export] public WorldEnvironment WorldEnv { get; set; }

    [Export] public int MaxChunksBuiltPerFrame { get; set; } = 2;

    // ─────────────────────────────────────────────────────────────────────────
    // ESTADO INTERNO
    // ─────────────────────────────────────────────────────────────────────────
    private Dictionary<Vector2I, Chunk> _loadedChunks = new();
    private Queue<Vector2I> _pendingChunks = new();
    private HashSet<Vector2I> _chunksInProgress = new();
    // (Web) trabajo de generación de cada chunk en curso (para poder pasar el del jugador al frente).
    private readonly Dictionary<Vector2I, FrameJobs.Job> _coopJobs = new();
    private bool _coopHeld = false;            // el jugador está retenido esperando el chunk bajo sus pies
    private ulong _coopHeldSinceMs = 0;
    private ulong _coopHoldCooldownUntilMs = 0;
    private Queue<(Chunk inst, Vector2I pos)> _chunkFinishQueue = new();

    // OBJECT POOLING: nodos Chunk reciclados (ocultos, dentro del árbol, listos
    // para ser reconfigurados con una nueva posición). Nunca se hace
    // Instantiate()/QueueFree() salvo la primerísima vez que se necesita un
    // nodo y el pool está vacío.
    private readonly Stack<Chunk> _chunkPool = new Stack<Chunk>();

    private StandardMaterial3D _matWater = new StandardMaterial3D();
    private StandardMaterial3D _matPureSolid = new StandardMaterial3D();
    private StandardMaterial3D _matSolidTrans = new StandardMaterial3D();

    // Generador de mundo: UNA sola instancia, inmutable y thread-safe, compartida
    // por todos los chunks y todos los hilos del WorkerThreadPool.
    private WorldGenerator _generator;

    private Vector2 _lastUpdatePos = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
    private float _updateThreshold = 8.0f;

    private int _maxThreads;
    private float _chunkCheckTimer = 0.0f;
    private const float ChunkCheckInterval = 0.1f;

    // # FIX línea 47 — Se elimina LodDistance ya que interfería con la generación en exploración continua.

    private Vector2I _lastSimChunkPos = new Vector2I(int.MaxValue, int.MaxValue);

    private const int UrgentRadius = 2;
    private const int ActivationBudgetPerFrame = 4;
    private readonly Queue<Chunk> _activationQueue = new Queue<Chunk>();
    private readonly HashSet<Chunk> _activationQueued = new HashSet<Chunk>();

    private const int SpawnReadyRadius = 2;
    private bool _playerPhysicsEnabled = false;
    private ulong _spawnWaitStartMs = 0; // inicio de la espera de colision del spawn

    // CARGA INTENSIVA: mientras la pantalla de carga tapa todo, se construyen
    // más chunks por frame y se usan más hilos (el jugador no está jugando).
    private bool _cargaIntensiva = false;

    // JUGADOR BLOQUEADO: mientras la pantalla de carga está visible el jugador
    // no debe poder moverse, romper/poner bloques ni sufrir nada. El teclado y el
    // mouse de Player se leen con Input.is_action_pressed() (sondeo) dentro de
    // _physics_process, así que bloquear los eventos no basta: hay que mantener
    // apagada su física hasta que la pantalla de carga termine.
    private bool _jugadorBloqueado = false;
    private const int ChunksBuiltPerFrameCarga = 12;
    // (Web, sin hilos) tiempo por fotograma dedicado a generar chunks. Con la pantalla de carga
    // puesta nadie juega, así que se puede gastar bastante más.
    private const ulong CoopGenBudgetUsec = 9000;
    private const ulong CoopGenBudgetCargaUsec = 22000;
    private const ulong CoopRebuildBudgetUsec = 4000;        // reconstruir mallas de secciones existentes
    private const ulong CoopRebuildBudgetCargaUsec = 12000;
    private const int CoopChunksBuiltPerFrame = 1;
    private const int CoopChunksBuiltPerFrameCarga = 6;
    private const int CoopActivationBudgetPerFrame = 2;
    private const int ChunkStartsPerFrameCarga = 8;
    private int _cacheDiscoRadio = -1;
    private int _cacheDiscoTotal = 0;
    private float _cacheProgresoTerreno = 0.0f;
    private ulong _cacheProgresoMs = 0;

    private readonly object _finishQueueLock = new object();

    private static readonly (Vector2I offset, int dx, int dz)[] NeighborDirs =
    {
        (new Vector2I(-1,  0), -1,  0),
        (new Vector2I( 1,  0),  1,  0),
        (new Vector2I( 0, -1),  0, -1),
        (new Vector2I( 0,  1),  0,  1),
    };

    // ─────────────────────────────────────────────────────────────────────────
    // INICIALIZACIÓN
    // ─────────────────────────────────────────────────────────────────────────
    public override void _Ready()
    {
        // NUEVO: el menú de pausa hace get_tree().paused = true para congelar
        // al jugador y a los mobs. Por defecto World heredaría ese pausado
        // (ProcessMode.Inherit) y su _Process() —que es quien de verdad
        // encola/desencola la construcción y descarga de chunks cada
        // frame— dejaría de correr mientras el menú está abierto. Eso hacía
        // que cambios de Distancia de Renderizado/Simulación o de Nubes
        // hechos desde Opciones (dentro de la pausa) quedaran guardados
        // pero invisibles hasta salir del mundo y volver a entrar.
        // Con Always, World sigue gestionando chunks aunque el árbol esté
        // pausado; el jugador y el ciclo día/noche se marcan Pausable más
        // abajo/en sus propios scripts para que ELLOS sí quedan congelados
        // (si no, heredarían este Always por ser hijos de World).
        ProcessMode = ProcessModeEnum.Always;

        // OPT: antes usaba hasta (núcleos_lógicos - 2), con tope 6. En un CPU
        // de 4 núcleos físicos / 8 lógicos (típico i5 con hyperthreading),
        // eso deja SOLO 2 hilos lógicos libres para el hilo principal (_process,
        // física, tu lógica de juego) Y el hilo de renderizado de Godot juntos
        // -- sobre-suscribe la CPU justo cuando más chunks se generan a la vez
        // (exactamente lo que pasa al subir de 8 a 16 de distancia de render),
        // robándole tiempo de CPU al hilo que de verdad necesita correr fluido
        // cada frame. Ahora se deja la mitad de los hilos lógicos (mínimo 1,
        // máximo 4) para generación de chunks, con más margen para el resto.
        _maxThreads = Chunk.HilosDeFondo();
        AplicarModoMononucleo(); // opción "Generar chunks: Mononúcleo"; debe ir antes de lanzar cualquier trabajo
        FrameJobs.Clear(); // trabajos de un mundo anterior (si lo hubo)
        if (FrameJobs.Cooperative)
            GD.Print(FrameJobs.ActivoPorOpcion
                ? "World: MODO MONONÚCLEO (opción de Gráficos): la generación de chunks se reparte por fotogramas con presupuesto de tiempo."
                : "World: MODO SIN HILOS (Web): la generación de chunks se reparte por fotogramas con presupuesto de tiempo.");

        CargarConfiguracionVideo(); // debe ir antes de SetupFog() y del primer CheckRadialGrid()

        SetupPersistence(); // debe ir antes de SetupGenerator(), porque decide la seed
        SetupMaterials();
        SetupGenerator();
        SetupFog();

        if (Player != null)
        {
            Player.SetPhysicsProcess(false);
            _playerPhysicsEnabled = false;
            // Spawn por defecto: justo encima del terreno en (0,0), no a una altura
            // fija. Con el relieve nuevo y=150 podía caer dentro de una montaña o a
            // 90 bloques de caída libre sobre el mar.
            float spawnY = _generator.SurfaceHeightAt(0, 0) + 3.0f;
            Player.GlobalPosition = LoadPlayerPosition(new Vector3(0.5f, spawnY, 0.5f));
            CheckRadialGrid(new Vector2(Player.GlobalPosition.X, Player.GlobalPosition.Z));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // CONFIGURACIÓN DE VIDEO (NUEVO)
    // ─────────────────────────────────────────────────────────────────────────
    // ConfiguracionJuego (autoload GDScript) solo empuja sus valores en vivo
    // cuando el jugador mueve un slider CON el mundo ya abierto. Si el ajuste
    // se cambió antes (desde el menú principal, sin mundo cargado) o viene
    // de settings.cfg, World nunca se enteraba y arrancaba con los valores
    // por defecto del Inspector. Por eso World se los pide él mismo al entrar.
    // "Generar chunks" tiene 4 opciones en Opciones > Gráficos; la 3 es "Mononúcleo" (ver menu_video.gd).
    // Se lee solo al entrar al mundo: cambiarla con el mundo abierto se aplica la próxima vez que se entre.
    private const int IndiceGenerarMononucleo = 3;

    private void AplicarModoMononucleo()
    {
        bool activar = false;
        if (HasNode("/root/ConfiguracionJuego"))
        {
            try { activar = (int)GetNode("/root/ConfiguracionJuego").Get("indice_constructor") == IndiceGenerarMononucleo; }
            catch (Exception) { activar = false; }
        }
        FrameJobs.SetMononucleoUsuario(activar);
    }

    private void CargarConfiguracionVideo()
    {
        if (!HasNode("/root/ConfiguracionJuego"))
        {
            GD.Print("World: no se encontró el Autoload 'ConfiguracionJuego'. Uso los valores del Inspector.");
            return;
        }

        var cfg = GetNode("/root/ConfiguracionJuego");
        RenderDistance = (int)cfg.Get("distancia_renderizado");
        SimulationDistance = (int)cfg.Get("distancia_simulacion");
        Chunk.PlantMaxChunks = ComputePlantMaxChunks();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PERSISTENCIA (NUEVO)
    // ─────────────────────────────────────────────────────────────────────────
    // Lee el mundo elegido en el menú (vía el Autoload "WorldManager", que es
    // GDScript) y prepara las rutas/seed para esta partida. Si no hay ningún
    // WorldManager en el árbol (por ejemplo, probando esta escena sola desde
    // el editor con F6), simplemente sigue todo como antes: sin persistencia.
    private void SetupPersistence()
    {
        if (!HasNode("/root/WorldManager"))
        {
            GD.Print("World: no se encontró el Autoload 'WorldManager'. Corriendo sin persistencia (seed aleatoria, sin guardado).");
            _persistenceEnabled = false;
            return;
        }

        var wm = GetNode("/root/WorldManager");
        string worldName = (string)wm.Get("mundo_actual");

        if (string.IsNullOrEmpty(worldName))
        {
            GD.Print("World: WorldManager existe pero 'mundo_actual' está vacío. Sin persistencia.");
            _persistenceEnabled = false;
            return;
        }

        _currentWorldName = worldName;
        _worldSeed = (int)wm.Call("get_world_seed", worldName);
        _worldType = (int)wm.Call("get_world_type", worldName);
        if (_worldType == WorldGenerator.TypeFlat)
            _flatSettings = ParseFlatSettings(wm.Call("get_flat_settings", worldName).AsGodotDictionary());
        if (_worldType == WorldGenerator.TypeSingleBiome)
            _singleBiome = wm.Call("get_single_biome", worldName).AsString();

        _worldSaveDir = ProjectSettings.GlobalizePath($"user://saves/{worldName}/chunks/");
        Directory.CreateDirectory(_worldSaveDir);

        _playerSavePath = ProjectSettings.GlobalizePath($"user://saves/{worldName}/player.json");

        _persistenceEnabled = true;
        GD.Print($"World: persistencia activa para el mundo '{worldName}' (seed={_worldSeed}).");
    }

    // ----------------------------------------------------------------
    // POSICIÓN DEL JUGADOR
    // ----------------------------------------------------------------
    private Vector3 LoadPlayerPosition(Vector3 fallbackSpawn)
    {
        if (!_persistenceEnabled || !File.Exists(_playerSavePath))
            return fallbackSpawn;

        try
        {
            string json = File.ReadAllText(_playerSavePath);
            var data = Json.ParseString(json).AsGodotDictionary();
            if (data.ContainsKey("pos_x") && data.ContainsKey("pos_y") && data.ContainsKey("pos_z"))
            {
                return new Vector3(
                    (float)data["pos_x"],
                    (float)data["pos_y"],
                    (float)data["pos_z"]
                );
            }
        }
        catch (Exception e)
        {
            GD.PushWarning($"World: no se pudo leer player.json ({e.Message}). Se usa el spawn por defecto.");
        }

        return fallbackSpawn;
    }

    private void SavePlayerData()
    {
        if (!_persistenceEnabled || Player == null) return;

        var data = new Godot.Collections.Dictionary
        {
            { "pos_x", Player.GlobalPosition.X },
            { "pos_y", Player.GlobalPosition.Y },
            { "pos_z", Player.GlobalPosition.Z }
        };

        try
        {
            File.WriteAllText(_playerSavePath, Json.Stringify(data));
        }
        catch (Exception e)
        {
            GD.PushWarning($"World: no se pudo guardar player.json ({e.Message}).");
        }
    }

    // Guarda al cerrar el juego (clic en la X de la ventana / Alt+F4).
    public override void _Notification(int what)
    {
        if (what == (int)NotificationWMCloseRequest)
        {
            SavePlayerData();
            FlushDirtySnapshots(); // NUEVO: no perder ediciones de los últimos < 4s antes de cerrar
        }
        else if (what == (int)NotificationExitTree)
        {
            // Si se sale del mundo en plena carga intensiva, el presupuesto de
            // mallas no debe quedarse subido para el próximo mundo.
            Chunk.SetCargaIntensiva(false);
        }
    }

    // NUEVO: guardado manual sin cerrar el juego. Lo llama el menú de pausa
    // (GDScript) cuando el jugador elige "Guardar y salir al menú principal":
    // vuelca la posición del jugador y cualquier edición de bloques pendiente
    // ANTES de cambiar de escena, para no perder nada de esta sesión.
    // Público (PascalCase) a propósito: así queda expuesto a GDScript igual
    // que GetPlaceableBlocksArray/GetBlockName en BlockRegistry.
    /// <summary>
    /// Se llama cuando se aplica un paquete de recursos: los chunks ya cargados reconstruyen su
    /// malla con las texturas nuevas (poco a poco, con el presupuesto normal de cada chunk).
    /// </summary>
    public void RecargarRecursosVisuales()
    {
        Chunk.LimpiarCacheGrupos();
        foreach (var kv in _loadedChunks)
            if (GodotObject.IsInstanceValid(kv.Value)) kv.Value.ForzarRemallado();
    }

    public void GuardarMundoAhora()
    {
        if (!_persistenceEnabled) return;
        SavePlayerData();
        FlushDirtySnapshots();
    }

    // ----------------------------------------------------------------
    // EDICIONES DE BLOQUES (romper/poner) — guardado por chunk
    // ----------------------------------------------------------------
    private string ChunkEditPath(Vector2I cPos) => $"{_worldSaveDir}{cPos.X}_{cPos.Y}.chunkedit";

    private Dictionary<Vector3I, float> LoadChunkEdits(Vector2I cPos)
    {
        var edits = new Dictionary<Vector3I, float>();
        if (!_persistenceEnabled) return edits;

        string path = ChunkEditPath(cPos);
        if (!File.Exists(path)) return edits;

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(',');
                if (parts.Length != 4) continue;

                int lx = int.Parse(parts[0], CultureInfo.InvariantCulture);
                int ly = int.Parse(parts[1], CultureInfo.InvariantCulture);
                int lz = int.Parse(parts[2], CultureInfo.InvariantCulture);
                float id = float.Parse(parts[3], CultureInfo.InvariantCulture);

                edits[new Vector3I(lx, ly, lz)] = id;
            }
        }
        catch (Exception e)
        {
            GD.PushWarning($"World: error leyendo ediciones de chunk {cPos} ({e.Message}). Se ignoran.");
        }

        return edits;
    }

    // OBSOLETO (se deja solo por compatibilidad de código, ya no se llama
    // desde ningún lado): reescribía el archivo .chunkedit completo en cada
    // bloque modificado. Reemplazado por el snapshot completo del chunk
    // (ChunkSnapshotPath / SaveChunkSnapshot / MarkChunkSnapshotDirty más
    // abajo), que además de guardar el bloque roto/puesto guarda el chunk
    // entero (terreno + árboles + cuevas), bufferizado cada
    // SnapshotFlushInterval segundos como sugería esta nota.
    private void SaveBlockEdit(Vector2I cPos, Vector3I localPos, float blockId)
    {
        if (!_persistenceEnabled) return;

        if (!_chunkEditsCache.TryGetValue(cPos, out var edits))
        {
            edits = LoadChunkEdits(cPos);
            _chunkEditsCache[cPos] = edits;
        }

        edits[localPos] = blockId;

        try
        {
            using var writer = new StreamWriter(ChunkEditPath(cPos), false);
            foreach (var kv in edits)
            {
                writer.WriteLine(string.Join(",",
                    kv.Key.X.ToString(CultureInfo.InvariantCulture),
                    kv.Key.Y.ToString(CultureInfo.InvariantCulture),
                    kv.Key.Z.ToString(CultureInfo.InvariantCulture),
                    kv.Value.ToString(CultureInfo.InvariantCulture)));
            }
        }
        catch (Exception e)
        {
            GD.PushWarning($"World: no se pudo guardar la edición del chunk {cPos} ({e.Message}).");
        }
    }

    private Dictionary<Vector3I, float> GetOrLoadChunkEdits(Vector2I cPos)
    {
        if (_chunkEditsCache.TryGetValue(cPos, out var cached))
            return cached;

        var loaded = LoadChunkEdits(cPos);
        _chunkEditsCache[cPos] = loaded;
        return loaded;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // SNAPSHOT COMPLETO DE CHUNK (NUEVO): terreno + árboles + cuevas + toda
    // edición del jugador, todo junto en un solo archivo ".chunkdata" por
    // chunk. Reemplaza al sistema de "regenerar por ruido y pisar con la lista
    // de ediciones" para chunks nuevos; ese sistema viejo (arriba) se conserva
    // solo para poder migrar partidas guardadas con la versión anterior.
    //
    // Formato en disco (todo little-endian, vía BinaryWriter/BinaryReader):
    //   int32   maxYUsed
    //   int32   cantidad de runs de vóxeles (RLE)
    //   repetido esa cantidad de veces:
    //     float32 blockId
    //     int32   longitud del run
    //   int32   cantidad de colores de tinte de follaje (256, o 0 si no hay)
    //   repetido esa cantidad de veces: float32 r, g, b
    //
    // El terreno voxel es, en su enorme mayoría, aire por encima de la
    // superficie y piedra por debajo: RLE sobre el array ya ordenado (X más
    // rápido, después Z, después Y) comprime esas capas horizontales casi
    // uniformes a un puñado de runs, así que el archivo resultante es mucho
    // más chico que el array crudo (16*16*384 floats ≈ 384 KB sin comprimir).
    // ─────────────────────────────────────────────────────────────────────────
    private string ChunkSnapshotPath(Vector2I cPos) => $"{_worldSaveDir}{cPos.X}_{cPos.Y}.chunkdata";

    /// <summary>
    /// Comprime (RLE) y escribe a disco el snapshot completo de un chunk.
    /// Se puede llamar tanto desde el hilo de generación (primera vez que
    /// existe el chunk) como desde el hilo principal (al volcar ediciones
    /// pendientes o al reciclar un chunk fuera de rango).
    /// </summary>
    private void SaveChunkSnapshot(Vector2I cPos, float[] voxels, int maxYUsed, Color[] foliageTint)
    {
        if (!_persistenceEnabled || voxels == null) return;

        // Un chunk liberado/reciclado deja _voxels vacío (DisposeData). Guardar eso
        // creaba un archivo con 0 runs que luego se leía como "0/98304 vóxeles".
        if (voxels.Length != Chunk.VoxelCount) return;

        string finalPath = ChunkSnapshotPath(cPos);
        // Nombre temporal único por hilo: dos guardados del mismo chunk a la vez no se
        // pisan, y no hace falta un candado global (que serializaba TODOS los guardados
        // de los hilos de generación y los hacía esperar entre sí).
        string tempPath = finalPath + "." + System.Environment.CurrentManagedThreadId + ".tmp";

        try
        {
            // Se escribe a un archivo temporal y al final se renombra: quien lea
            // el snapshot ve el archivo viejo completo o el nuevo completo, nunca
            // uno a medio escribir (File.Create lo vaciaba al instante).
            {
            using (var stream = new FileStream(tempPath, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None))
            using (var writer = new BinaryWriter(stream))
            {

            writer.Write(maxYUsed);

            // Primera pasada: contar runs para escribir el conteo antes que los datos.
            int runCount = 0;
            {
                int i = 0;
                while (i < voxels.Length)
                {
                    float v = voxels[i];
                    int j = i + 1;
                    while (j < voxels.Length && voxels[j] == v) j++;
                    runCount++;
                    i = j;
                }
            }
            writer.Write(runCount);

            int k = 0;
            while (k < voxels.Length)
            {
                float v = voxels[k];
                int j = k + 1;
                while (j < voxels.Length && voxels[j] == v) j++;
                writer.Write(v);
                writer.Write(j - k);
                k = j;
            }

            if (foliageTint != null)
            {
                writer.Write(foliageTint.Length);
                foreach (var c in foliageTint)
                {
                    writer.Write(c.R);
                    writer.Write(c.G);
                    writer.Write(c.B);
                }
            }
            else
            {
                writer.Write(0);
            }
            }

            File.Move(tempPath, finalPath, true);
            }
        }
        catch (Exception e)
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* nada */ }
            GD.PushWarning($"World: no se pudo guardar el snapshot del chunk {cPos} ({e.Message}).");
        }
    }

    /// <summary>
    /// SaveChunkSnapshot repartido en pasos (solo se usa sin hilos / Web). Genera EXACTAMENTE el
    /// mismo archivo: la compresión RLE de 98.304 vóxeles se hace en trozos de 12.288, y luego se
    /// escribe de una vez (son pocos miles de pares).
    /// </summary>
    private IEnumerable<bool> SaveChunkSnapshotSteps(Vector2I cPos, float[] voxels, int maxYUsed, Color[] foliageTint)
    {
        if (!_persistenceEnabled || voxels == null) yield break;
        if (voxels.Length != Chunk.VoxelCount) yield break;

        FrameJobs.SetPhase("snapshot.rle");
        var runVals = new List<float>();
        var runLens = new List<int>();
        int total = voxels.Length;
        float curV = voxels[0];
        int curLen = 0;
        int pos = 0;
        while (pos < total)
        {
            int end = Math.Min(pos + 12288, total);
            for (; pos < end; pos++)
            {
                float v = voxels[pos];
                if (v == curV) curLen++;
                else
                {
                    runVals.Add(curV);
                    runLens.Add(curLen);
                    curV = v;
                    curLen = 1;
                }
            }
            yield return true;
        }
        runVals.Add(curV);
        runLens.Add(curLen);

        FrameJobs.SetPhase("snapshot.escribir");
        string finalPath = ChunkSnapshotPath(cPos);
        string tempPath = finalPath + "." + System.Environment.CurrentManagedThreadId + ".tmp";

        try
        {
            using (var stream = new FileStream(tempPath, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.None))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(maxYUsed);
                writer.Write(runVals.Count);
                for (int r = 0; r < runVals.Count; r++)
                {
                    writer.Write(runVals[r]);
                    writer.Write(runLens[r]);
                }

                if (foliageTint != null)
                {
                    writer.Write(foliageTint.Length);
                    foreach (var c in foliageTint)
                    {
                        writer.Write(c.R);
                        writer.Write(c.G);
                        writer.Write(c.B);
                    }
                }
                else
                {
                    writer.Write(0);
                }
            }

            File.Move(tempPath, finalPath, true);
        }
        catch (Exception e)
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* nada */ }
            GD.PushWarning($"World: no se pudo guardar el snapshot del chunk {cPos} ({e.Message}).");
        }
        yield return true;
    }

    // ── Avisos de snapshots: baratos y limitados ────────────────────────────
    // GD.PushWarning desde C# arma un backtrace completo (caro) y se llamaba una
    // vez por chunk dañado DESDE LOS HILOS DE CARGA. Ahora se imprimen solo los
    // primeros avisos con GD.Print (sin backtrace).
    private static int _snapshotLogCount = 0;
    private static void LogSnapshotProblem(string msg)
    {
        int n = System.Threading.Interlocked.Increment(ref _snapshotLogCount);
        if (n <= 5) GD.Print("[World] " + msg);
        else if (n == 6) GD.Print("[World] (más avisos de snapshots omitidos para no frenar la carga)");
    }

    /// <summary>Borra un snapshot ilegible: el chunk se regenera y se guarda bien de nuevo.</summary>
    private void DeleteBadSnapshot(Vector2I cPos)
    {
        try { File.Delete(ChunkSnapshotPath(cPos)); } catch { }
    }

    /// <summary>Marca un chunk cargado como pendiente de re-guardar su snapshot completo.</summary>
    private void MarkChunkSnapshotDirty(Vector2I cPos)
    {
        if (!_persistenceEnabled) return;
        _dirtySnapshotChunks.Add(cPos);
    }

    /// <summary>Vuelca a disco todos los snapshots marcados como sucios que sigan cargados.</summary>
    private void FlushDirtySnapshots()
    {
        if (_dirtySnapshotChunks.Count == 0) return;

        foreach (var cPos in _dirtySnapshotChunks)
        {
            if (_loadedChunks.TryGetValue(cPos, out var chunk) && GodotObject.IsInstanceValid(chunk))
            {
                var (voxels, maxYUsed, tint) = chunk.CaptureSnapshot();
                SaveChunkSnapshot(cPos, voxels, maxYUsed, tint);
            }
            // Si ya no está cargado (se descargó sin pasar por acá primero, caso
            // límite), no hay nada que guardar: DrainUnloadQueue ya lo vuelca
            // antes de devolverlo al pool (ver más abajo).
        }
        _dirtySnapshotChunks.Clear();
    }

    /// <summary>Vuelca el snapshot de un único chunk ya mismo, sin esperar al timer (usado antes de reciclarlo o al cerrar el juego).</summary>
    private void FlushChunkSnapshotNow(Vector2I cPos, Chunk chunk)
    {
        if (!_persistenceEnabled) return;
        if (!_dirtySnapshotChunks.Remove(cPos)) return; // no estaba sucio, nada que hacer
        if (!GodotObject.IsInstanceValid(chunk)) return;

        var (voxels, maxYUsed, tint) = chunk.CaptureSnapshot();
        SaveChunkSnapshot(cPos, voxels, maxYUsed, tint);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // LOOP PRINCIPAL
    // ─────────────────────────────────────────────────────────────────────────
    // ─────────────────────────────────────────────────────────────────────────
    // DIAGNÓSTICO DE RENDIMIENTO (solo si se arranca con MOTORVOXEL_DIAG=1)
    // ─────────────────────────────────────────────────────────────────────────
    // Imprime en la consola cada 3 s una línea con los números clave, para
    // comparar el mismo juego en dos equipos / sistemas y saber si el cuello
    // de botella es la CPU (tiempo de proceso alto), la GPU (FPS bajos con
    // tiempo de proceso bajo) o la generación de chunks (colas llenas).
    //   Linux:   MOTORVOXEL_DIAG=1 ./motor\ voxel\ linux.x86_64
    //   Windows: set MOTORVOXEL_DIAG=1 && MotorVoxel.exe   (o desde PowerShell: $env:MOTORVOXEL_DIAG=1)
    //   Cualquiera de estas 3 formas lo activa (usa la que te funcione):
    //     1) Variable de entorno:   MOTORVOXEL_DIAG=1 ./motor\ voxel\ linux.x86_64
    //     2) Argumento de consola:  ./motor\ voxel\ linux.x86_64 -- --diag
    //     3) Un archivo vacío llamado  diag.flag  junto al ejecutable (touch diag.flag)
    //   Además de la consola, escribe lo mismo en  user://diag_rendimiento.txt  (la ruta
    //   real se imprime al entrar al mundo), para poder mandarlo sin copiar de la terminal.
    private static readonly bool _diagActiva = ActivarDiag();
    private static bool ActivarDiag() { bool a = DiagSolicitada(); Prof.Enabled = a; return a; }
    private static string _diagRutaArchivo = null;

    private static bool DiagSolicitada()
    {
        try
        {
            string env = System.Environment.GetEnvironmentVariable("MOTORVOXEL_DIAG");
            if (!string.IsNullOrEmpty(env) && env != "0" && !env.Equals("false", StringComparison.OrdinalIgnoreCase)) return true;

            foreach (var a in OS.GetCmdlineUserArgs()) if (a == "--diag") return true;
            foreach (var a in OS.GetCmdlineArgs()) if (a == "--diag") return true;

            if (File.Exists(System.IO.Path.Combine(OS.GetExecutablePath().GetBaseDir(), "diag.flag"))) return true;
            if (File.Exists(ProjectSettings.GlobalizePath("user://diag.flag"))) return true;
        }
        catch { }
        return false;
    }

    /// <summary>Imprime en consola y, si el diagnóstico está activo, lo agrega al archivo de registro.</summary>
    private static void DiagLog(string linea)
    {
        GD.Print(linea);
        try
        {
            _diagRutaArchivo ??= ProjectSettings.GlobalizePath("user://diag_rendimiento.txt");
            File.AppendAllText(_diagRutaArchivo, $"{DateTime.Now:HH:mm:ss} {linea}{System.Environment.NewLine}");
        }
        catch { }
    }
    private double _diagTimer = 0.0;
    private bool _diagCabeceraImpresa = false;
    private double _diagMaxDelta = 0.0;
    private int _diagGc0, _diagGc1, _diagGc2;
    private double _diagGcPausaMs;

    /// <summary>
    /// Solo Linux: estado de energía y frecuencias leído de /sys. Sirve para saber si
    /// el equipo está limitado por ahorro de batería / temperatura, que en laptops
    /// baja el rendimiento sin que ningún monitor muestre la CPU ni la GPU saturadas.
    ///   energia   enchufado | BATERIA
    ///   cpu_mhz   frecuencia media ahora (tope = máximo permitido por el perfil)
    ///   gov/epp   gobernador y preferencia de energía (power = ahorro máximo)
    ///   gpu_mhz   frecuencia actual / máxima de la gráfica Intel integrada
    ///   temp      temperatura máxima de los sensores
    /// </summary>
    private static string InfoEnergiaLinux()
    {
        if (!OperatingSystem.IsLinux()) return "";
        try
        {
            static string Leer(string path)
            {
                try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
                catch { return null; }
            }

            string ac = "?";
            if (Directory.Exists("/sys/class/power_supply"))
                foreach (var d in Directory.GetDirectories("/sys/class/power_supply"))
                    if (Leer(d + "/type") == "Mains") { ac = Leer(d + "/online") == "1" ? "enchufado" : "BATERIA"; break; }

            double suma = 0; int n = 0;
            foreach (var d in Directory.GetDirectories("/sys/devices/system/cpu", "cpu*"))
                if (long.TryParse(Leer(d + "/cpufreq/scaling_cur_freq"), out long khz)) { suma += khz / 1000.0; n++; }
            string cpuMhz = n > 0 ? $"{suma / n:F0}" : "?";
            string tope = long.TryParse(Leer("/sys/devices/system/cpu/cpu0/cpufreq/scaling_max_freq"), out long maxKhz) ? $"{maxKhz / 1000}" : "?";
            string gov = Leer("/sys/devices/system/cpu/cpu0/cpufreq/scaling_governor") ?? "?";
            string epp = Leer("/sys/devices/system/cpu/cpu0/cpufreq/energy_performance_preference") ?? "?";

            string gpuCur = null, gpuMax = null;
            for (int c = 0; c < 3 && gpuCur == null; c++)
            {
                gpuCur = Leer($"/sys/class/drm/card{c}/gt_cur_freq_mhz");
                gpuMax = Leer($"/sys/class/drm/card{c}/gt_max_freq_mhz");
            }

            double tMax = 0;
            if (Directory.Exists("/sys/class/thermal"))
                foreach (var d in Directory.GetDirectories("/sys/class/thermal", "thermal_zone*"))
                    if (long.TryParse(Leer(d + "/temp"), out long mc)) tMax = Math.Max(tMax, mc / 1000.0);

            return $" energia={ac} cpu_mhz={cpuMhz}(tope {tope}) gov={gov} epp={epp} gpu_mhz={gpuCur ?? "?"}/{gpuMax ?? "?"} temp={tMax:F0}C";
        }
        catch { return " energia=?"; }
    }

    private void DiagnosticoRendimiento(double delta)
    {
        if (!_diagCabeceraImpresa)
        {
            _diagCabeceraImpresa = true;
            _diagRutaArchivo ??= ProjectSettings.GlobalizePath("user://diag_rendimiento.txt");
            DiagLog($"[DIAG] SO={OS.GetName()} CPU='{OS.GetProcessorName()}' hilos_logicos={OS.GetProcessorCount()} " +
                     $"hilos_de_fondo={Chunk.HilosDeFondo()} video='{RenderingServer.GetVideoAdapterName()}' " +
                     $"driver_video='{RenderingServer.GetVideoAdapterApiVersion()}' render={RenderingServer.GetCurrentRenderingMethod()} " +
                     $"ventana={DisplayServer.GetName()} vsync={DisplayServer.WindowGetVsyncMode()} " +
                     $"dist_render={RenderDistance} build={(OS.IsDebugBuild() ? "DEBUG" : "release")} archivo='{_diagRutaArchivo}'");
        }

        if (delta > _diagMaxDelta) _diagMaxDelta = delta;

        _diagTimer += delta;
        if (_diagTimer < 3.0) return;
        _diagTimer = 0.0;

        // Recolector de basura de .NET: cuántas pasadas hizo en estos 3 s y cuánto
        // tiempo tuvo TODOS los hilos detenidos. Si gc_pausa es alto y coincide con
        // frames largos (frame_max), los tirones vienen de la memoria, no de los hilos.
        int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
        double gcPausaMs = GC.GetTotalPauseDuration().TotalMilliseconds;
        string gcInfo = $"gc0={gc0 - _diagGc0} gc1={gc1 - _diagGc1} gc2={gc2 - _diagGc2} gc_pausa={gcPausaMs - _diagGcPausaMs:F0}ms frame_max={_diagMaxDelta * 1000.0:F0}ms";
        _diagGc0 = gc0; _diagGc1 = gc1; _diagGc2 = gc2; _diagGcPausaMs = gcPausaMs; _diagMaxDelta = 0.0;

        double fps = Performance.GetMonitor(Performance.Monitor.TimeFps);
        double procMs = Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0;
        double fisMs = Performance.GetMonitor(Performance.Monitor.TimePhysicsProcess) * 1000.0;
        double drawCalls = Performance.GetMonitor(Performance.Monitor.RenderTotalDrawCallsInFrame);
        double objetos = Performance.GetMonitor(Performance.Monitor.RenderTotalObjectsInFrame);
        double prims = Performance.GetMonitor(Performance.Monitor.RenderTotalPrimitivesInFrame);
        double nodos = Performance.GetMonitor(Performance.Monitor.ObjectNodeCount);

        DiagLog($"[DIAG] fps={fps:F0} proceso={procMs:F1}ms fisica={fisMs:F1}ms draw_calls={drawCalls:F0} " +
                 $"objetos={objetos:F0} primitivas={prims:F0} nodos={nodos:F0} " +
                 $"chunks_cargados={_loadedChunks.Count} pendientes={_pendingChunks.Count} en_curso={_chunksInProgress.Count} " +
                 $"mallas_por_subir={Chunk.PendingInitialUploads} {gcInfo}{InfoEnergiaLinux()}");
        // Qué gasta el hilo principal en estos 3 s (n = veces, total = ms, max = la peor vez)
        DiagLog("[DIAG]   PROF:" + Prof.Dump());
    }

    public override void _Process(double delta)
    {
        long _pp = Prof.Now();
        try { ProcessImpl(delta); } finally { Prof.Add("World._Process", _pp); }
    }

    private void ProcessImpl(double delta)
    {
        if (Player == null) return;
        FrameJobs.BeginFrame(delta); // regulador de presupuesto (Web)
        Chunk.PlayerWorldPos = Player.GlobalPosition; // lo usa Chunk para no demorar reconstrucciones cercanas

        if (_diagActiva) DiagnosticoRendimiento(delta);

        // ANTI-TIRONES: publicar el chunk del jugador y drenar las mallas
        // iniciales pendientes con presupuesto de tiempo (más cercanas primero).
        Chunk.PlayerPosInChunks = new Vector2(Player.GlobalPosition.X / 16.0f, Player.GlobalPosition.Z / 16.0f);
        Chunk.PlayerChunkPos = new Vector2I(
            (int)Mathf.Floor(Chunk.PlayerPosInChunks.X),
            (int)Mathf.Floor(Chunk.PlayerPosInChunks.Y));
        UpdatePlayerForward();
        Chunk.DrainInitialUploads();

        int activatedThisFrame = 0;
        int activationBudget = FrameJobs.Cooperative ? CoopActivationBudgetPerFrame : ActivationBudgetPerFrame;
        while (_activationQueue.Count > 0 && activatedThisFrame < activationBudget)
        {
            var chunk = _activationQueue.Dequeue();
            _activationQueued.Remove(chunk);
            if (GodotObject.IsInstanceValid(chunk))
                chunk.SetSimulationState(true);
            activatedThisFrame++;
        }

        int builtThisFrame = 0;
        lock (_finishQueueLock)
        {
            int maxBuilt = _cargaIntensiva ? Math.Max(MaxChunksBuiltPerFrame, ChunksBuiltPerFrameCarga) : MaxChunksBuiltPerFrame;
            if (FrameJobs.Cooperative) maxBuilt = _cargaIntensiva ? CoopChunksBuiltPerFrameCarga : CoopChunksBuiltPerFrame;
            while (_chunkFinishQueue.Count > 0 && builtThisFrame < maxBuilt)
            {
                var (inst, pos) = DequeueBestFinished();

                if (GodotObject.IsInstanceValid(inst))
                    AddChunkToWorld(inst, pos);

                builtThisFrame++;
            }
        }

        DrainUnloadQueue();

        if (!_playerPhysicsEnabled)
            TryEnablePlayerPhysics();

        _chunkCheckTimer += (float)delta;
        if (_chunkCheckTimer >= ChunkCheckInterval)
        {
            _chunkCheckTimer = 0.0f;
            var pPos2d = new Vector2(Player.GlobalPosition.X, Player.GlobalPosition.Z);

            if (pPos2d.DistanceTo(_lastUpdatePos) > _updateThreshold)
            {
                _lastUpdatePos = pPos2d;
                CheckRadialGrid(pPos2d);
                CleanFarChunks(pPos2d);
            }

            TryUpdateSimulationDistance();
        }

        // Re-ordenar pendientes ~3 veces/s: el jugador gira o cambia de rumbo y
        // lo que antes estaba "detrás" ahora está delante.
        _resortTimer += (float)delta;
        if (_resortTimer >= 0.3f)
        {
            _resortTimer = 0.0f;
            if (_pendingChunks.Count > 1) ResortPending();
        }

        // (Web) PRIORIDAD MÁXIMA al chunk bajo el jugador: se genera primero y con presupuesto grande, y
        // si todavía no está listo el jugador queda retenido para no caer al vacío.
        bool coopCritical = false;
        if (FrameJobs.Cooperative && _playerPhysicsEnabled && !_jugadorBloqueado)
            coopCritical = CoopProtectPlayer();
        FrameJobs.GenBoost = coopCritical;

        // Hasta 2 arranques por frame (antes 1): rellena los hilos libres más rápido
        // sin acumular trabajo en el hilo principal.
        int started = 0;
        int maxStarts = _cargaIntensiva ? ChunkStartsPerFrameCarga : 2;
        // Durante la carga intensiva se usan más hilos (todos menos uno), igual en
        // Windows y Linux.
        int hilosCarga = OS.GetProcessorCount() - 1;
        int maxHilos = _cargaIntensiva ? Math.Max(_maxThreads, Mathf.Clamp(hilosCarga, 1, 8)) : _maxThreads;
        if (FrameJobs.Cooperative)
        {
            // Sin hilos los chunks se generan UNO A LA VEZ (por pasos). Se dejan como mucho 2-3 en
            // curso: uno generándose y otro esperando a añadirse al mundo. El resto sigue en la
            // cola _pendingChunks, que se reordena según hacia dónde mira/avanza el jugador.
            maxStarts = 1;
            maxHilos = _cargaIntensiva ? 3 : 2;
        }
        while (_pendingChunks.Count > 0 && _chunksInProgress.Count < maxHilos && started < maxStarts)
        {
            var nextPos = _pendingChunks.Dequeue();
            StartThreadedChunkGeneration(nextPos);
            started++;
        }

        // (Web) Ejecuta pasos de generación mientras quede presupuesto de tiempo en este fotograma.
        if (FrameJobs.Cooperative)
        {
            // Primero las reconstrucciones de mallas ya existentes (romper/poner bloques, costuras de
            // vecinos), luego la generación de chunks nuevos. Cada una con su presupuesto de tiempo.
            long _pr = Prof.Now();
            FrameJobs.PumpRebuild(_cargaIntensiva ? CoopRebuildBudgetCargaUsec : FrameJobs.Scaled(CoopRebuildBudgetUsec));
            Prof.Add("coop.rebuild", _pr);

            long _pg = Prof.Now();
            // Mientras hay una edición del jugador esperando, la generación de chunks cede casi todo el
            // tiempo: poner/romper bloques tiene prioridad sobre cargar terreno nuevo (salvo que el jugador
            // esté esperando el chunk bajo sus pies: ahí manda coopCritical).
            ulong genBudget = _cargaIntensiva ? CoopGenBudgetCargaUsec : FrameJobs.Scaled(CoopGenBudgetUsec);
            if (FrameJobs.EditPending && !coopCritical) genBudget = 1500UL;
            FrameJobs.Pump(genBudget);
            Prof.Add("coop.gen", _pg);
        }

        // NUEVO: autosave de posición del jugador cada PlayerAutoSaveInterval segundos.
        if (_persistenceEnabled)
        {
            _playerSaveTimer += (float)delta;
            if (_playerSaveTimer >= PlayerAutoSaveInterval)
            {
                _playerSaveTimer = 0.0f;
                SavePlayerData();
            }

            // NUEVO: volcado en lote de los snapshots de chunk marcados como
            // sucios (bloques rotos/puestos desde el último flush).
            _snapshotFlushTimer += (float)delta;
            if (_snapshotFlushTimer >= SnapshotFlushInterval)
            {
                _snapshotFlushTimer = 0.0f;
                FlushDirtySnapshots();
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MATERIALES, RUIDOS, NIEBLA
    // ─────────────────────────────────────────────────────────────────────────
    private void SetupFog()
    {
        if (WorldEnv == null || WorldEnv.Environment == null) return;
        var env = WorldEnv.Environment;
        env.FogEnabled = true;
        env.FogDensity = 0.001f;
        float maxDist = RenderDistance * 16.0f;
        env.FogDepthEnd = maxDist;
        env.FogDepthBegin = maxDist * 0.85f;

        // OPTIMIZACIÓN VIRTUAL: Glow Suave habilitado
        env.GlowEnabled = GlowActivo;
        env.GlowIntensity = 0.4f;
        env.GlowBloom = 0.05f;
    }

    private void SetupMaterials()
    {
        _matPureSolid.VertexColorUseAsAlbedo = true;

        // AGUA: textura ORIGINAL de Minecraft (water_still.png, definida en el JSON
        // del bloque Agua). Viene en gris y el color azul sale del tinte por bioma
        // que Chunk.cs pone en el color de cada vértice (ver BiomeColors.Water).
        _matWater.AlbedoTexture = BlockRegistry.Instance?.GetBlockTexture(12.8f);
        _matWater.TextureFilter = BaseMaterial3D.TextureFilterEnum.Nearest;
        _matWater.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
        _matWater.AlbedoColor = new Color(1, 1, 1, 0.75f);
        _matWater.VertexColorUseAsAlbedo = true;

        _matSolidTrans.VertexColorUseAsAlbedo = true;
        _matSolidTrans.Transparency = BaseMaterial3D.TransparencyEnum.Alpha;
    }

    /// <summary>Convierte el Dictionary de WorldManager.get_flat_settings() en la configuración del generador.</summary>
    private static WorldGenerator.FlatSettings ParseFlatSettings(Godot.Collections.Dictionary d)
    {
        var fs = new WorldGenerator.FlatSettings();
        fs.Layers.Clear();
        if (d.ContainsKey("layers"))
        {
            foreach (var item in d["layers"].AsGodotArray())
            {
                var pair = item.AsGodotArray();
                if (pair.Count < 2) continue;
                float id = (float)pair[0].AsDouble();
                int h = (int)pair[1].AsDouble();
                if (h > 0) fs.Layers.Add((id, h));
            }
        }
        if (d.ContainsKey("biome")) fs.BiomeId = d["biome"].AsString();
        return fs;
    }

    private void SetupGenerator()
    {
        // Si venimos del menú de mundos usamos la seed guardada en world.json.
        // Si no (probando la escena suelta con F6), una seed aleatoria.
        int s = _persistenceEnabled ? _worldSeed : (int)GD.Randi();

        int wType = _persistenceEnabled ? _worldType : WorldGenerator.TypeDefault;
        _generator = new WorldGenerator(s, wType, _flatSettings, _singleBiome);

        GD.Print($"World: generador listo (seed={s}, tipo de mundo={wType}, nivel del mar={WorldGenerator.SeaLevel}).");
        if (_diagActiva)
            GD.Print("[DIAG] ACTIVO: cada 3 s se imprime una línea [DIAG] (y se guarda en user://diag_rendimiento.txt).");
        else
            GD.Print("[DIAG] apagado. Para activarlo: MOTORVOXEL_DIAG=1 ./motor\\ voxel\\ linux.x86_64   |   ./motor\\ voxel\\ linux.x86_64 -- --diag   |   crear un archivo vacío 'diag.flag' junto al ejecutable.");
        if (OS.IsDebugBuild())
            GD.Print("[World] AVISO: esta es una build DEBUG (la ventana dice \"(DEBUG)\"). Corre bastante más lento que una release, " +
                     "sobre todo al generar chunks. Para medir rendimiento real exporta con \"Export With Debug\" DESMARCADO.");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ACTIVACIÓN DE FÍSICA DEL JUGADOR
    // ─────────────────────────────────────────────────────────────────────────
    private void TryEnablePlayerPhysics()
    {
        if (_jugadorBloqueado) return; // la pantalla de carga sigue visible

        int pX = (int)Mathf.Floor(Player.GlobalPosition.X / 16.0f);
        int pZ = (int)Mathf.Floor(Player.GlobalPosition.Z / 16.0f);

        for (int x = pX - SpawnReadyRadius; x <= pX + SpawnReadyRadius; x++)
            for (int z = pZ - SpawnReadyRadius; z <= pZ + SpawnReadyRadius; z++)
                if (!_loadedChunks.ContainsKey(new Vector2I(x, z)))
                    return;

        // NUEVO: esperar a que el chunk del jugador tenga su colision construida
        // (si no, el jugador cae al vacio antes de que exista el suelo, sobre todo en
        // el superplano donde el spawn esta a solo 3 bloques). Con tope de 8 s por si
        // alguna seccion nunca termina.
        if (_spawnWaitStartMs == 0) _spawnWaitStartMs = Time.GetTicksMsec();
        bool esperaAgotada = Time.GetTicksMsec() - _spawnWaitStartMs > 8000;
        if (!esperaAgotada
            && _loadedChunks.TryGetValue(new Vector2I(pX, pZ), out var spawnChunk)
            && GodotObject.IsInstanceValid(spawnChunk)
            && spawnChunk.IsCollisionPending())
            return;

        _playerPhysicsEnabled = true;
        Player.SetPhysicsProcess(true);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // PROGRESO DE CARGA INICIAL (lo consulta la pantalla de carga, GDScript)
    // ─────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// Qué tan listo está el terreno alrededor del jugador, de 0.0 a 1.0.
    /// Cuenta los chunks ya cargados dentro del mismo radio que exige
    /// TryEnablePlayerPhysics (SpawnReadyRadius), así que llega a 1.0 justo
    /// cuando el jugador puede empezar a moverse. PantallaCarga.gd lo llama
    /// cada frame para llenar la barra de progreso.
    /// </summary>
    public float GetSpawnLoadProgress()
    {
        if (_playerPhysicsEnabled) return 1.0f;
        if (Player == null) return 0.0f;

        int pX = (int)Mathf.Floor(Player.GlobalPosition.X / 16.0f);
        int pZ = (int)Mathf.Floor(Player.GlobalPosition.Z / 16.0f);

        int total = 0;
        int loaded = 0;
        for (int x = pX - SpawnReadyRadius; x <= pX + SpawnReadyRadius; x++)
            for (int z = pZ - SpawnReadyRadius; z <= pZ + SpawnReadyRadius; z++)
            {
                total++;
                if (_loadedChunks.ContainsKey(new Vector2I(x, z))) loaded++;
            }

        return total > 0 ? (float)loaded / total : 0.0f;
    }

    /// <summary>
    /// La pantalla de carga lo activa al empezar a esperar el terreno y lo
    /// desactiva al terminar: acelera la generación/subida de chunks mientras
    /// el jugador no puede moverse.
    /// </summary>
    public void BloquearJugador(bool bloquear)
    {
        _jugadorBloqueado = bloquear;
        if (Player == null) return;

        if (bloquear)
            Player.SetPhysicsProcess(false);
        else if (_playerPhysicsEnabled)
            Player.SetPhysicsProcess(true);
        // Si todavía no estaba listo el terreno del spawn, TryEnablePlayerPhysics()
        // activa la física sola en cuanto lo esté.
    }

    public void SetCargaIntensiva(bool activa)
    {
        _cargaIntensiva = activa;
        Chunk.SetCargaIntensiva(activa);
    }

    /// <summary>
    /// Qué parte (0.0 a 1.0) de TODO el terreno dentro de la distancia de
    /// renderizado elegida en Opciones ya está listo (chunk creado y malla
    /// subida). La pantalla de carga lo usa para esperar "una gran parte"
    /// del terreno en vez de solo los 25 chunks del spawn.
    /// </summary>
    public float GetTerrainLoadProgress()
    {
        if (Player == null) return 0.0f;

        // Se recalcula como mucho cada 100 ms (con distancias grandes hay
        // decenas de miles de chunks y no hace falta contarlos cada frame).
        ulong now = Time.GetTicksMsec();
        if (now - _cacheProgresoMs < 100 && _cacheDiscoRadio == RenderDistance)
            return _cacheProgresoTerreno;
        _cacheProgresoMs = now;

        int R = Math.Max(1, RenderDistance);
        if (_cacheDiscoRadio != R)
        {
            int total = 0;
            for (int x = -R; x <= R; x++)
                for (int z = -R; z <= R; z++)
                    if (Mathf.Sqrt(x * x + z * z) <= R) total++;
            _cacheDiscoTotal = total;
            _cacheDiscoRadio = R;
        }

        int pX = (int)Mathf.Floor(Player.GlobalPosition.X / 16.0f);
        int pZ = (int)Mathf.Floor(Player.GlobalPosition.Z / 16.0f);
        int loaded = 0;
        foreach (var kv in _loadedChunks)
        {
            float dx = kv.Key.X - pX, dz = kv.Key.Y - pZ;
            if (Mathf.Sqrt(dx * dx + dz * dz) <= R) loaded++;
        }

        // Un chunk cuenta como listo cuando su malla ya se subió a la GPU.
        loaded = Math.Max(0, loaded - Chunk.PendingInitialUploads);

        _cacheProgresoTerreno = _cacheDiscoTotal > 0
            ? Mathf.Clamp((float)loaded / _cacheDiscoTotal, 0.0f, 1.0f)
            : 1.0f;
        return _cacheProgresoTerreno;
    }

    /// <summary>True cuando el terreno inicial ya cargó y el jugador puede moverse.</summary>
    public bool IsSpawnReady() => _playerPhysicsEnabled;

    // ─────────────────────────────────────────────────────────────────────────
    // HELPERS DE DISTANCIA
    // ─────────────────────────────────────────────────────────────────────────
    private bool IsInSimulationRange(Vector2I cPos)
    {
        if (Player == null) return false;
        int pX = (int)Mathf.Floor(Player.GlobalPosition.X / 16.0f);
        int pZ = (int)Mathf.Floor(Player.GlobalPosition.Z / 16.0f);
        return new Vector2(cPos.X - pX, cPos.Y - pZ).Length() <= SimulationDistance;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Llamado desde ConfiguracionJuego (GDScript) cuando el jugador cambia
    // RenderDistance/SimulationDistance desde el menú de opciones. Sin esto,
    // el cambio quedaba guardado en la propiedad pero no se notaba hasta que
    // el jugador caminaba más de _updateThreshold, porque CheckRadialGrid()
    // y CleanFarChunks() solo se disparan por movimiento en _Process.
    // ─────────────────────────────────────────────────────────────────────────
    public void ForzarActualizacionChunks()
    {
        if (Player == null) return;

        // NUEVO: SetupFog() calculaba dónde empieza/termina la niebla en
        // base a RenderDistance, pero solo se llamaba una vez en _Ready().
        // Sin esto, aunque los chunks de más se carguen o descarguen bien,
        // la niebla seguía cortando a la distancia VIEJA — subir la
        // distancia de renderizado no se notaba porque los chunks nuevos
        // quedaban tapados por la niebla de antes, y bajarla dejaba una
        // niebla de sobra más allá de donde ya no hay chunks.
        SetupFog();

        var pPos2d = new Vector2(Player.GlobalPosition.X, Player.GlobalPosition.Z);
        _lastUpdatePos = pPos2d;
        CheckRadialGrid(pPos2d);
        CleanFarChunks(pPos2d);
        UpdateSimulationDistance(pPos2d);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // GESTIÓN DE CHUNKS
    // ─────────────────────────────────────────────────────────────────────────
    public void CheckRadialGrid(Vector2 pPos2d)
    {
        int pX = (int)Mathf.Floor(pPos2d.X / 16.0f);
        int pZ = (int)Mathf.Floor(pPos2d.Y / 16.0f);
        var pChunkPos = new Vector2I(pX, pZ);

        var newPending = new List<Vector2I>();
        for (int x = pX - RenderDistance; x <= pX + RenderDistance; x++)
            for (int z = pZ - RenderDistance; z <= pZ + RenderDistance; z++)
            {
                var pos = new Vector2I(x, z);
                if (!_loadedChunks.ContainsKey(pos) && !_chunksInProgress.Contains(pos) && !_poisonedChunks.Contains(pos))
                {
                    float dist = new Vector2(pChunkPos.X, pChunkPos.Y).DistanceTo(new Vector2(x, z));
                    if (dist <= RenderDistance)
                        newPending.Add(pos);
                }
            }

        Chunk.PlayerPosInChunks = pPos2d / 16.0f; // orden coherente aunque se llame antes del primer frame
        newPending.Sort((a, b) => Chunk.LoadPriority(a).CompareTo(Chunk.LoadPriority(b)));

        _pendingChunks.Clear();
        foreach (var pos in newPending)
            _pendingChunks.Enqueue(pos);
    }

    private float _resortTimer = 0.0f;

    // ─────────────────────────────────────────────────────────────────────────
    // (WEB) PROTECCIÓN DEL JUGADOR: el chunk bajo sus pies tiene prioridad máxima
    // ─────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// Sin hilos los chunks llegan despacio y un jugador que corre o vuela puede adelantarlos y
    /// caer al vacío. Aquí: (1) el chunk bajo el jugador (y el que alcanzará en ~0.6 s) se genera
    /// primero, saltándose la cola y el tope de chunks en curso; (2) mientras el chunk de sus pies
    /// no esté listo (cargado y con colisión), se retiene al jugador (como al empezar la partida);
    /// (3) la generación usa un presupuesto grande hasta que esté listo. Devuelve true si el chunk
    /// bajo los pies aún no está listo.
    /// </summary>
    private bool CoopProtectPlayer()
    {
        Vector2I feet = Chunk.PlayerChunkPos;
        bool feetReady = _loadedChunks.TryGetValue(feet, out var feetChunk)
                         && GodotObject.IsInstanceValid(feetChunk)
                         && !feetChunk.IsCollisionPending();

        // Chunk hacia el que va (si se mueve): también se pide con prioridad, sin retener por él.
        Vector2I ahead = feet;
        var vel = new Vector2(Player.Velocity.X, Player.Velocity.Z);
        if (vel.Length() > 2.0f)
        {
            Vector2 p2 = new Vector2(Player.GlobalPosition.X, Player.GlobalPosition.Z) + vel * 0.6f;
            ahead = new Vector2I((int)Mathf.Floor(p2.X / 16.0f), (int)Mathf.Floor(p2.Y / 16.0f));
        }

        if (!feetReady) MakeChunkCritical(feet);
        if (ahead != feet && !_loadedChunks.ContainsKey(ahead)) MakeChunkCritical(ahead);

        ulong now = Time.GetTicksMsec();
        if (!feetReady && now >= _coopHoldCooldownUntilMs)
        {
            if (!_coopHeld)
            {
                _coopHeld = true;
                _coopHeldSinceMs = now;
                Player.SetPhysicsProcess(false);
            }
            else if (now - _coopHeldSinceMs > 6000UL)
            {
                // Seguro anti-bloqueo: si en 6 s no se logra, se suelta al jugador y no se vuelve a
                // retener durante 5 s.
                _coopHeld = false;
                _coopHoldCooldownUntilMs = now + 5000UL;
                Player.SetPhysicsProcess(true);
            }
        }
        else if (_coopHeld)
        {
            _coopHeld = false;
            Player.SetPhysicsProcess(true);
        }

        return !feetReady;
    }

    /// <summary>Hace que el chunk 'pos' se genere ya: lo saca de la cola de pendientes, lo arranca sin
    /// respetar el tope de chunks en curso y lo pasa al frente de la cola de generación.</summary>
    private void MakeChunkCritical(Vector2I pos)
    {
        if (_loadedChunks.ContainsKey(pos) || _poisonedChunks.Contains(pos)) return;

        if (_chunksInProgress.Contains(pos))
        {
            if (_coopJobs.TryGetValue(pos, out var running)) FrameJobs.PromoteToFront(running);
            return;
        }

        if (_pendingChunks.Count > 0)
        {
            var keep = new Queue<Vector2I>(_pendingChunks.Count);
            foreach (var pp in _pendingChunks) if (pp != pos) keep.Enqueue(pp);
            _pendingChunks = keep;
        }

        StartThreadedChunkGeneration(pos);
        if (_coopJobs.TryGetValue(pos, out var started)) FrameJobs.PromoteToFront(started);
    }

    private void ResortPending()
    {
        var list = new List<Vector2I>(_pendingChunks);
        list.Sort((a, b) => Chunk.LoadPriority(a).CompareTo(Chunk.LoadPriority(b)));
        _pendingChunks.Clear();
        foreach (var p in list) _pendingChunks.Enqueue(p);
    }

    // Dirección de avance/mirada en el plano XZ. Si va rápido se usa su velocidad
    // (hacia donde REALMENTE avanza); si no, hacia donde mira la cámara.
    private void UpdatePlayerForward()
    {
        var v = new Vector2(Player.Velocity.X, Player.Velocity.Z);
        Vector2 dir;
        if (v.Length() > 3.0f) dir = v.Normalized();
        else
        {
            var cam = GetViewport()?.GetCamera3D();
            if (cam == null) return;
            var f = -cam.GlobalTransform.Basis.Z;
            var f2 = new Vector2(f.X, f.Z);
            if (f2.Length() < 0.05f) return; // mirando recto arriba/abajo: se conserva la anterior
            dir = f2.Normalized();
        }
        // Suavizado para que no salte al girar la cámara de golpe.
        // (Web) suavizado mayor: así girar la cámara no cambia de golpe el orden de carga de los chunks.
        var mixed = Chunk.PlayerForward.Lerp(dir, FrameJobs.Cooperative ? 0.08f : 0.25f);
        if (mixed.Length() > 0.01f) Chunk.PlayerForward = mixed.Normalized();
    }

    // Saca de la cola de terminados el chunk de mayor prioridad (llamar dentro
    // de lock(_finishQueueLock)); la cola es corta (<= hilos + presupuesto).
    private (Chunk inst, Vector2I pos) DequeueBestFinished()
    {
        int n = _chunkFinishQueue.Count;
        if (n == 1) return _chunkFinishQueue.Dequeue();

        (Chunk inst, Vector2I pos) best = default;
        float bestP = float.MaxValue;
        var keep = new Queue<(Chunk inst, Vector2I pos)>(n);
        for (int i = 0; i < n; i++)
        {
            var it = _chunkFinishQueue.Dequeue();
            float p = Chunk.LoadPriority(it.pos);
            if (p < bestP)
            {
                if (bestP != float.MaxValue) keep.Enqueue(best);
                best = it; bestP = p;
            }
            else keep.Enqueue(it);
        }
        _chunkFinishQueue = keep;
        return best;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // OBJECT POOLING DE CHUNKS (OPTIMIZACIÓN)
    // ─────────────────────────────────────────────────────────────────────────
    // Saca un Chunk reciclado del pool. Si no hay ninguno disponible, recién
    // ahí se hace Instantiate() — y se añade al árbol UNA SOLA VEZ en toda su
    // vida. A partir de ese momento el nodo nunca se vuelve a quitar del árbol,
    // solo se oculta/muestra (Visibilidad Diferida).
    private Chunk RentChunkFromPool()
    {
        while (_chunkPool.Count > 0)
        {
            var c = _chunkPool.Pop();
            if (GodotObject.IsInstanceValid(c))
                return c;
        }

        var inst = ChunkScene.Instantiate<Chunk>();
        // NUEVO: World ahora es ProcessMode.Always (ver _Ready) para que la
        // cola de chunks siga corriendo con el juego en pausa. Sin esto, el
        // Chunk (hijo de World) heredaría ese Always y su simulación de
        // agua seguiría corriendo con el mundo "congelado" en el menú de
        // pausa. Se fija explícito en Pausable para que SÍ se congele.
        inst.ProcessMode = ProcessModeEnum.Pausable;
        AddChild(inst);
        inst.Visible = false;
        return inst;
    }

    // Devuelve un Chunk al pool en vez de destruirlo. El nodo se queda
    // colgado de World, invisible y con sus secciones (mesh/colisión)
    // desactivadas pero intactas, listo para ser reconfigurado.
    private void ReturnChunkToPool(Chunk inst)
    {
        if (!GodotObject.IsInstanceValid(inst)) return;
        inst.PrepareForPool();
        _chunkPool.Push(inst);
    }

    private void StartThreadedChunkGeneration(Vector2I cPos)
    {
        _chunksInProgress.Add(cPos);

        var inst = RentChunkFromPool();

        // # FIX línea 233 — Se restaura Configure original sin parámetros restrictivos de LOD.
        inst.Configure(cPos, _generator, _matWater, _matPureSolid, _matSolidTrans);
        inst.SetSimulationState(IsInSimulationRange(cPos));

        // NUEVO: el archivo (edits viejos o snapshot completo) se abre ACÁ, en
        // el hilo principal, porque leer archivos / llamar al WorldManager
        // (GDScript) desde el WorkerThreadPool no es seguro. Se le pasan al
        // chunk ya resueltos; descomprimir el snapshot (que es solo copiar
        // runs a un array, sin cómputo de ruido) si pasa dentro del hilo de
        // trabajo, para no bloquear el frame con chunks grandes.
        //
        // PRIORIDAD: si existe un snapshot completo (.chunkdata) para esta
        // posición, se usa ese y no se toca el sistema de ediciones viejo en
        // absoluto — el snapshot ya tiene adentro terreno + árboles + cuevas +
        // cualquier edición que el jugador haya hecho la última vez. Si no
        // existe (chunk nunca visitado, o partida guardada con la versión
        // vieja del juego), se cae al camino de siempre: generar por ruido y
        // pisar con la lista de bloques editados (si la hay), y al terminar
        // se guarda el snapshot completo para que la próxima vez sea instantáneo.
        string snapshotPath = ChunkSnapshotPath(cPos);
        bool hasSnapshotFile = _persistenceEnabled && File.Exists(snapshotPath);

        Dictionary<Vector3I, float> legacyEdits = null;
        byte[] snapshotBytes = null;

        if (hasSnapshotFile)
        {
            try { snapshotBytes = File.ReadAllBytes(snapshotPath); }
            catch (Exception e)
            {
                LogSnapshotProblem($"no se pudo leer el snapshot del chunk {cPos} ({e.Message}). Se regenera desde cero.");
                snapshotBytes = null;
            }
        }

        if (snapshotBytes == null)
        {
            // Sin snapshot completo todavía: camino de compatibilidad de
            // siempre (ruido + ediciones sueltas del formato viejo, si hay).
            legacyEdits = GetOrLoadChunkEdits(cPos);
            inst.SetSavedEdits(legacyEdits);
        }

        if (FrameJobs.Cooperative)
        {
            // SIN HILOS (Web): el mismo trabajo, repartido en pasos que FrameJobs.Pump() ejecuta
            // con presupuesto de tiempo en World._Process (ver GenerarChunkCoop).
            var coopRes = new GenResult();
            int coopGen = inst.CurrentGeneration;

            // Enlazar ya los vecinos que existen para que el chunk se malle con sus datos reales
            // (ver Chunk.PreLinkNeighbor): evita reconstruir luego las costuras de este chunk.
            inst.BeginPreLink();
            for (int pl = 0; pl < 4; pl++)
            {
                int pdx = pl == 0 ? -1 : (pl == 1 ? 1 : 0);
                int pdz = pl == 2 ? -1 : (pl == 3 ? 1 : 0);
                if (_loadedChunks.TryGetValue(new Vector2I(cPos.X + pdx, cPos.Y + pdz), out var preNbr))
                    inst.PreLinkNeighbor(pdx, pdz, preNbr);
            }
            var coopJob = FrameJobs.Enqueue(
                GenerarChunkCoop(inst, cPos, snapshotBytes, coopRes),
                () => inst.IsGenerationStale(coopGen),
                ok =>
                {
                    _coopJobs.Remove(cPos);
                    if (ok && coopRes.Ok)
                    {
                        if (GodotObject.IsInstanceValid(this) && GodotObject.IsInstanceValid(inst))
                            QueueChunkFinish(inst, cPos);
                        else if (GodotObject.IsInstanceValid(inst))
                            inst.QueueFree(); // Caso límite: el World ya no existe (cierre del juego).
                    }
                    else
                    {
                        AbortChunkGeneration(inst, cPos);
                    }
                });
            _coopJobs[cPos] = coopJob;
            return;
        }

        WorkerThreadPool.AddTask(Callable.From(() =>
        {
            // FIX HUECOS: antes, si este hilo hacía un `return` temprano o lanzaba una
            // excepción, la posición se quedaba en _chunksInProgress PARA SIEMPRE:
            //   1) ese chunk nunca volvía a pedirse -> un hueco permanente en el mundo;
            //   2) cada posición "fantasma" ocupaba una de las _maxThreads plazas, así
            //      que tras unas pocas fallas el mundo dejaba de generar chunks por
            //      completo y todo se ponía cada vez más lento al moverse.
            // Ahora CUALQUIER salida que no termine el chunk avisa al hilo principal
            // para que libere la posición (y el chunk vuelva al pool).
            bool terminado = false;
            try
            {
                terminado = GenerarChunkEnHilo(inst, cPos, snapshotBytes);
            }
            catch (Exception e)
            {
                LogSnapshotProblem($"error generando el chunk {cPos}: {e.GetType().Name}: {e.Message}");
            }

            if (!terminado)
                Callable.From(() => AbortChunkGeneration(inst, cPos)).CallDeferred();
        }), true); // prioridad alta: generar chunks nuevos no debe esperar detrás de reconstrucciones de malla
    }

    /// <summary>
    /// SIN HILOS (Web): lo mismo que GenerarChunkEnHilo, pero partido en pasos pequeños. Cada
    /// 'yield return' devuelve el control al motor; FrameJobs.Pump() lo reanuda en el siguiente
    /// paso mientras quede presupuesto de tiempo del fotograma. Deja coopRes.Ok = true si el chunk
    /// quedó listo para encolarse; si no, el llamador libera la posición (AbortChunkGeneration).
    /// </summary>
    private IEnumerable<bool> GenerarChunkCoop(Chunk inst, Vector2I cPos, byte[] snapshotBytes, GenResult coopRes)
    {
        coopRes.Ok = false;
        if (!GodotObject.IsInstanceValid(inst)) yield break;

        if (snapshotBytes != null)
        {
            FrameJobs.SetPhase("snapshot.leer");
            var decoded = DecodeChunkSnapshot(snapshotBytes, cPos);
            yield return true;

            if (decoded.HasValue)
            {
                foreach (bool step in inst.GenerateAndBuildFromSnapshotSteps(decoded.Value.voxels, decoded.Value.maxYUsed, decoded.Value.foliageTint))
                    yield return true;
            }
            else
            {
                // Snapshot corrupto/ilegible: se cae a generación normal.
                var grA = new GenResult();
                foreach (bool step in inst.GenerateAndBuildSteps(grA)) yield return true;
                if (!grA.Ok) yield break;
                var snapA = inst.CaptureSnapshot();
                foreach (bool step in SaveChunkSnapshotSteps(cPos, snapA.voxels, snapA.maxYUsed, snapA.foliageTint))
                    yield return true;
            }
        }
        else
        {
            var grB = new GenResult();
            foreach (bool step in inst.GenerateAndBuildSteps(grB)) yield return true;
            if (!grB.Ok) yield break;
            yield return true;

            // Primera vez que existe este chunk: se guarda el snapshot completo
            // (terreno + árboles + cuevas ya resueltos) para que la próxima carga
            // sea una simple lectura de disco.
            var snapB = inst.CaptureSnapshot();
            foreach (bool step in SaveChunkSnapshotSteps(cPos, snapB.voxels, snapB.maxYUsed, snapB.foliageTint))
                yield return true;
        }

        coopRes.Ok = true;
    }

    /// <summary>
    /// Trabajo pesado de un chunk (corre en un hilo del WorkerThreadPool). Devuelve
    /// true si el chunk quedó encolado para terminarse en el hilo principal, y
    /// false si hay que abortar y liberar la posición (ver AbortChunkGeneration).
    /// </summary>
    private bool GenerarChunkEnHilo(Chunk inst, Vector2I cPos, byte[] snapshotBytes)
    {
        if (!GodotObject.IsInstanceValid(inst)) return false;

        if (snapshotBytes != null)
        {
            var decoded = DecodeChunkSnapshot(snapshotBytes, cPos);
            if (decoded.HasValue)
            {
                inst.GenerateAndBuildFromSnapshot(decoded.Value.voxels, decoded.Value.maxYUsed, decoded.Value.foliageTint);
            }
            else
            {
                // Snapshot corrupto/ilegible: se cae a generación normal.
                if (!inst.GenerateAndBuild()) return false; // chunk liberado/reciclado a mitad: se descarta
                var (voxels, maxYUsed, tint) = inst.CaptureSnapshot();
                SaveChunkSnapshot(cPos, voxels, maxYUsed, tint);
            }
        }
        else
        {
            if (!inst.GenerateAndBuild()) return false; // chunk liberado/reciclado a mitad: se descarta

            // Primera vez que existe este chunk: se guarda el snapshot completo
            // (terreno + árboles + cuevas ya resueltos) para que la próxima carga
            // sea una simple lectura de disco.
            var (voxels, maxYUsed, tint) = inst.CaptureSnapshot();
            SaveChunkSnapshot(cPos, voxels, maxYUsed, tint);
        }

        Callable.From(() =>
        {
            if (GodotObject.IsInstanceValid(this) && GodotObject.IsInstanceValid(inst))
                QueueChunkFinish(inst, cPos);
            else if (GodotObject.IsInstanceValid(inst))
                inst.QueueFree(); // Caso límite: el World ya no existe (cierre del juego).
        }).CallDeferred();
        return true;
    }

    // Posiciones que fallaron varias veces seguidas: se dejan de reintentar para
    // no gastar CPU en bucle con un chunk "envenenado".
    private readonly Dictionary<Vector2I, int> _chunkFailures = new();
    private readonly HashSet<Vector2I> _poisonedChunks = new();
    private const int MaxChunkRetries = 3;

    /// <summary>Hilo principal: una generación no terminó. Libera la posición para que
    /// CheckRadialGrid la vuelva a pedir (hasta MaxChunkRetries veces) y recicla el chunk.</summary>
    private void AbortChunkGeneration(Chunk inst, Vector2I cPos)
    {
        _chunksInProgress.Remove(cPos);

        int fails = _chunkFailures.TryGetValue(cPos, out var f) ? f + 1 : 1;
        _chunkFailures[cPos] = fails;
        if (fails >= MaxChunkRetries)
        {
            _poisonedChunks.Add(cPos);
            LogSnapshotProblem($"el chunk {cPos} falló {fails} veces; se deja de reintentar.");
        }

        if (GodotObject.IsInstanceValid(inst) && !_chunkPool.Contains(inst))
            ReturnChunkToPool(inst);
    }

    /// <summary>
    /// Descomprime (RLE) unos bytes de snapshot ya leídos del disco. Separado
    /// de LoadChunkSnapshot() para poder correr esta parte (la única que pesa
    /// algo de CPU) dentro del WorkerThreadPool en vez del hilo principal.
    /// </summary>
    private (float[] voxels, int maxYUsed, Color[] foliageTint)? DecodeChunkSnapshot(byte[] bytes, Vector2I cPos)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            using var reader = new BinaryReader(stream);

            int maxYUsed = reader.ReadInt32();

            int runCount = reader.ReadInt32();
            var voxels = new float[Chunk.VoxelCount];
            int idx = 0;
            for (int i = 0; i < runCount; i++)
            {
                float blockId = reader.ReadSingle();
                int runLen = reader.ReadInt32();

                if (idx + runLen > voxels.Length)
                {
                    LogSnapshotProblem($"snapshot corrupto para chunk {cPos} (runs exceden tamaño del chunk). Se regenera.");
                    DeleteBadSnapshot(cPos);
                    return null;
                }

                for (int j = 0; j < runLen; j++)
                    voxels[idx++] = blockId;
            }

            if (idx != voxels.Length)
            {
                LogSnapshotProblem($"snapshot incompleto para chunk {cPos} ({idx}/{voxels.Length} vóxeles). Se regenera.");
                DeleteBadSnapshot(cPos);
                return null;
            }

            int tintCount = reader.ReadInt32();
            Color[] tint = null;
            if (tintCount > 0)
            {
                tint = new Color[tintCount];
                for (int i = 0; i < tintCount; i++)
                    tint[i] = new Color(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            }

            return (voxels, maxYUsed, tint);
        }
        catch (Exception e)
        {
            LogSnapshotProblem($"error decodificando snapshot del chunk {cPos} ({e.Message}). Se regenera desde cero.");
            DeleteBadSnapshot(cPos);
            return null;
        }
    }

    private void QueueChunkFinish(Chunk inst, Vector2I cPos)
    {
        lock (_finishQueueLock)
            _chunkFinishQueue.Enqueue((inst, cPos));
    }

    private void AddChunkToWorld(Chunk inst, Vector2I pos)
    {
        long _pa = Prof.Now();
        ulong _tn = Time.GetTicksUsec();
        try { AddChunkToWorldImpl(inst, pos); }
        finally
        {
            Prof.Add("AddChunkToWorld", _pa);
            FrameJobs.Note("add.chunk", Time.GetTicksUsec() - _tn);
        }
    }
    private void AddChunkToWorldImpl(Chunk inst, Vector2I pos)
    {
        if (!_loadedChunks.ContainsKey(pos))
        {
            // El nodo ya es hijo de World desde que se rentó del pool (o desde
            // su primera instanciación). Aquí solo se reposiciona y se revela:
            // ningún add_child/remove_child en tiempo real mientras el jugador
            // se mueve.
            inst.GlobalPosition = new Vector3(pos.X * 16, 0, pos.Y * 16);
            inst.Visible = true;
            _loadedChunks[pos] = inst;

            // OPT-2: Configurar sombra según distancia al jugador
            UpdateChunkShadow(inst, pos);

            foreach (var (offset, dx, dz) in NeighborDirs)
            {
                var nPos = pos + offset;
                if (_loadedChunks.TryGetValue(nPos, out var neighbor) &&
                    GodotObject.IsInstanceValid(neighbor))
                {
                    inst.SetNeighbor(dx, dz, neighbor);
                    neighbor.SetNeighbor(-dx, -dz, inst);
                    // Solo las secciones con cara expuesta REAL en la costura
                    // (antes: todas las del vecino, por cada chunk nuevo).
                    inst.RefreshSeamWith(neighbor, dx, dz);
                }
            }
        }
        else
        {
            // Posición duplicada (carrera entre hilos): el chunk vuelve al pool
            // en vez de destruirse.
            ReturnChunkToPool(inst);
        }
        _chunksInProgress.Remove(pos);
        if (_chunkFailures.Count > 0) _chunkFailures.Remove(pos); // cargó bien: se borra su historial de fallos
    }

    private void TryUpdateSimulationDistance()
    {
        if (Player == null) return;
        int pX = (int)Mathf.Floor(Player.GlobalPosition.X / 16.0f);
        int pZ = (int)Mathf.Floor(Player.GlobalPosition.Z / 16.0f);
        var pChunk = new Vector2I(pX, pZ);

        if (pChunk == _lastSimChunkPos) return;
        _lastSimChunkPos = pChunk;

        UpdateSimulationDistance(new Vector2(Player.GlobalPosition.X, Player.GlobalPosition.Z));
    }

    private void UpdateSimulationDistance(Vector2 pPos2d)
    {
        var pChunkPos = pPos2d / 16.0f;
        Chunk.PlantMaxChunks = ComputePlantMaxChunks(); // se adapta si el jugador cambia RenderDistance
        foreach (var kv in _loadedChunks)
        {
            var chunk = kv.Value;
            if (!GodotObject.IsInstanceValid(chunk)) continue;

            float dist = new Vector2(kv.Key.X, kv.Key.Y).DistanceTo(pChunkPos);
            bool shouldSim = dist <= SimulationDistance;

            // OPT-2: actualizar sombra cuando el jugador se mueve de chunk
            chunk.SetShadowCastingEnabled(dist <= ShadowDistance);
            chunk.RefreshPlantVisibility();

            if (!shouldSim)
            {
                _activationQueued.Remove(chunk);
                chunk.SetSimulationState(false);
            }
            else if (dist <= UrgentRadius)
            {
                _activationQueued.Remove(chunk);
                chunk.SetSimulationState(true);
            }
            else
            {
                if (!_activationQueued.Contains(chunk))
                {
                    _activationQueued.Add(chunk);
                    _activationQueue.Enqueue(chunk);
                }
            }
        }
    }

    // OPT-2: Activa/desactiva sombra de un chunk según su distancia al jugador.
    private void UpdateChunkShadow(Chunk chunk, Vector2I pos)
    {
        if (!GodotObject.IsInstanceValid(chunk)) return;
        if (Player == null) { chunk.SetShadowCastingEnabled(true); return; }

        float pX = Player.GlobalPosition.X / 16.0f;
        float pZ = Player.GlobalPosition.Z / 16.0f;
        float dist = new Vector2(pos.X - pX, pos.Y - pZ).Length();
        chunk.SetShadowCastingEnabled(dist <= ShadowDistance);
        Chunk.PlantMaxChunks = ComputePlantMaxChunks();
        chunk.RefreshPlantVisibility();
    }

    private readonly Queue<Vector2I> _unloadQueue = new Queue<Vector2I>();

    private void CleanFarChunks(Vector2 pPos2d)
    {
        float limit = RenderDistance + 1.0f;
        var pChunkPos = pPos2d / 16.0f;

        foreach (var kv in _loadedChunks)
        {
            if (new Vector2(kv.Key.X, kv.Key.Y).DistanceTo(pChunkPos) > limit)
                _unloadQueue.Enqueue(kv.Key);
        }
    }

    private const int ChunksUnloadedPerFrame = 3;
    private void DrainUnloadQueue()
    {
        int removed = 0;
        while (_unloadQueue.Count > 0 && removed < ChunksUnloadedPerFrame)
        {
            var pos = _unloadQueue.Dequeue();

            if (!_loadedChunks.TryGetValue(pos, out var chunk)) continue;
            if (_chunksInProgress.Contains(pos)) continue;

            _loadedChunks.Remove(pos);

            foreach (var (offset, dx, dz) in NeighborDirs)
            {
                var nPos = pos + offset;
                if (_loadedChunks.TryGetValue(nPos, out var neighbor) &&
                    GodotObject.IsInstanceValid(neighbor))
                {
                    neighbor.SetNeighbor(-dx, -dz, null);
                }
            }

            if (GodotObject.IsInstanceValid(chunk))
            {
                // NUEVO: si este chunk tiene ediciones sin volcar todavía (el
                // jugador rompió/puso un bloque hace menos de
                // SnapshotFlushInterval segundos), hay que guardarlas AHORA:
                // ReturnChunkToPool → PrepareForPool() vacía el array de
                // vóxeles del chunk para reciclarlo, así que después de esta
                // línea ya no habría de dónde leerlas.
                FlushChunkSnapshotNow(pos, chunk);

                // En vez de destruir el nodo, se devuelve al pool: queda oculto
                // y con su colisión desactivada, pero listo para reutilizarse
                // de inmediato en la próxima posición que necesite generarse.
                ReturnChunkToPool(chunk);
            }

            removed++;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // MODIFICACIÓN DE BLOQUES
    // ─────────────────────────────────────────────────────────────────────────
    /// <summary>
    /// Punto de entrada para colocar/romper bloques (lo llama el jugador). Ademas del
    /// cambio en si, aplica las reglas de las PLANTAS cruzadas, igual que Minecraft:
    ///   - una planta solo se coloca sobre un suelo valido (y las altas, con aire arriba);
    ///   - al colocar una planta alta se crean las 2 mitades, y al romper una cae la otra;
    ///   - si se quita el bloque de abajo, la planta de encima desaparece (en cadena);
    ///   - hierba marina y algas solo se colocan dentro del agua.
    /// </summary>
    public void ModifyBlock(Vector3I globalPos, float blockId)
    {
        var reg = BlockRegistry.Instance;
        if (reg == null || (!BlockRegistry.IsPlantId(blockId) && blockId > 0.0f && !HasPlantAroundCheck(globalPos)))
        {
            ModifyBlockRaw(globalPos, blockId);
            return;
        }

        float old = GetBlockGlobal(globalPos);

        // ── colocar una planta ──────────────────────────────────────────
        if (BlockRegistry.IsPlantId(blockId) && reg.IsPlant(blockId))
        {
            if (old < 0.0f) return; // chunk sin cargar
            bool aquatic = reg.IsAquaticPlant(blockId);
            bool oldIsWater = old >= 12.8f && old < 16.0f;
            if (aquatic ? !oldIsWater : (old > 0.0f && !reg.IsReplaceablePlant(old))) return;
            if (!aquatic && old >= 12.8f && old < 16.0f) return;

            float below = GetBlockGlobal(globalPos + Vector3I.Down);
            if (!reg.CanPlantStandOn(blockId, below)) return;

            // Caña de azucar: sobre arena/tierra/pasto solo si el bloque de abajo tiene
            // AGUA al lado (N/S/E/O). Sobre otra caña no hace falta (se apila).
            if (reg.RequiresAdjacentWater(blockId) && !Mathf.IsEqualApprox(below, blockId))
            {
                var soilPos = globalPos + Vector3I.Down;
                bool hasWater = false;
                foreach (var d in new[] { Vector3I.Right, Vector3I.Left, Vector3I.Forward, Vector3I.Back })
                {
                    float n = GetBlockGlobal(soilPos + d);
                    if (n >= 12.8f && n < 16.0f) { hasWater = true; break; }
                }
                if (!hasWater) return;
            }

            float partner = reg.GetTallPartner(blockId);
            if (partner > 0.0f && !reg.IsTallTop(blockId))
            {
                var topPos = globalPos + Vector3I.Up;
                float topOld = GetBlockGlobal(topPos);
                bool topOk = aquatic ? (topOld >= 12.8f && topOld < 16.0f)
                                     : (topOld == 0.0f || reg.IsReplaceablePlant(topOld));
                if (!topOk) return;
                ModifyBlockRaw(globalPos, blockId);
                ModifyBlockRaw(topPos, partner);
                return;
            }
            ModifyBlockRaw(globalPos, blockId);
            return;
        }

        // ── romper / reemplazar cualquier otra cosa ─────────────────────
        if (old > 0.0f && reg.IsPlant(old))
        {
            // Mitad de arriba de una planta alta: se lleva tambien la de abajo.
            if (reg.IsTallTop(old))
            {
                var bp = globalPos + Vector3I.Down;
                if (Mathf.IsEqualApprox(GetBlockGlobal(bp), reg.GetTallPartner(old)))
                    RemovePlantCell(bp, reg);
            }
        }

        ModifyBlockRaw(globalPos, blockId);

        // Lo que haya encima pierde el suelo si ya no es valido.
        var abovePos = globalPos + Vector3I.Up;
        float above = GetBlockGlobal(abovePos);
        if (above > 0.0f && reg.IsPlant(above) && !reg.CanPlantStandOn(above, blockId))
            RemovePlantCell(abovePos, reg);
    }

    /// <summary>Un chunk cambio por su cuenta (ticks aleatorios): marcarlo para guardar su snapshot.</summary>
    public void NotifyChunkChanged(Vector2I cPos) => MarkChunkSnapshotDirty(cPos);

    // Solo hace falta pasar por la logica de plantas si hay una planta en la celda o encima.
    private bool HasPlantAroundCheck(Vector3I pos)
    {
        float here = GetBlockGlobal(pos);
        if (here > 0.0f && BlockRegistry.IsPlantId(here)) return true;
        float above = GetBlockGlobal(pos + Vector3I.Up);
        return above > 0.0f && BlockRegistry.IsPlantId(above);
    }

    /// <summary>Quita una planta (aire, o agua si era submarina) y, en cadena, las que se apoyaban en ella.</summary>
    private void RemovePlantCell(Vector3I pos, BlockRegistry reg)
    {
        float id = GetBlockGlobal(pos);
        if (id <= 0.0f || !reg.IsPlant(id)) return;
        float replacement = reg.IsAquaticPlant(id) ? 12.8f : 0.0f;
        ModifyBlockRaw(pos, replacement);

        var abovePos = pos + Vector3I.Up;
        float above = GetBlockGlobal(abovePos);
        if (above > 0.0f && reg.IsPlant(above) && !reg.CanPlantStandOn(above, replacement))
            RemovePlantCell(abovePos, reg);
    }

    private void ModifyBlockRaw(Vector3I globalPos, float blockId)
    {
        var cPos = new Vector2I(
            (int)Mathf.Floor(globalPos.X / 16.0f),
            (int)Mathf.Floor(globalPos.Z / 16.0f)
        );
        if (_loadedChunks.TryGetValue(cPos, out var chunk) && GodotObject.IsInstanceValid(chunk))
        {
            var localPos = new Vector3I(Posmod(globalPos.X, 16), globalPos.Y, Posmod(globalPos.Z, 16));
            Chunk.NoteEdit(); // (Web) ANTES de editar: así la edición ya se marca como del jugador
            chunk.SetBlockAndUpdate(localPos, blockId);

            // NUEVO: en vez de reescribir una lista de ediciones sueltas
            // (formato viejo), se marca el chunk entero como "sucio" para que
            // su snapshot completo (terreno + árboles + esta edición) se
            // vuelque a disco en el próximo flush por lote — ver
            // FlushDirtySnapshots(), llamado cada SnapshotFlushInterval
            // segundos y también al descargar o cerrar el chunk. Así el
            // archivo guardado siempre contiene el bloque que el jugador puso,
            // sin tener que reescribir el chunk completo en cada click.
            MarkChunkSnapshotDirty(cPos);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // ACTUALIZACIÓN DE CHUNKS VECINOS EN BORDES
    // ─────────────────────────────────────────────────────────────────────────
    public void NotifyChunkBorderUpdate(Vector2I cPos, Vector3I localPos)
    {
        var offsets = new List<Vector2I>(2);
        if (localPos.X == 0) offsets.Add(new Vector2I(-1, 0));
        if (localPos.X == 15) offsets.Add(new Vector2I(1, 0));
        if (localPos.Z == 0) offsets.Add(new Vector2I(0, -1));
        if (localPos.Z == 15) offsets.Add(new Vector2I(0, 1));

        foreach (var offset in offsets)
        {
            var neighborPos = cPos + offset;
            if (_loadedChunks.TryGetValue(neighborPos, out var neighbor) &&
                GodotObject.IsInstanceValid(neighbor))
            {
                neighbor.MarkSectionDirtyAtY(localPos.Y);
            }
        }
    }

    public float GetBlockGlobal(Vector3I worldPos)
    {
        var cPos = new Vector2I(
            (int)Mathf.Floor(worldPos.X / 16.0f),
            (int)Mathf.Floor(worldPos.Z / 16.0f)
        );
        if (_loadedChunks.TryGetValue(cPos, out var chunk) && GodotObject.IsInstanceValid(chunk))
        {
            var localPos = new Vector3I(Posmod(worldPos.X, 16), worldPos.Y, Posmod(worldPos.Z, 16));
            return chunk.GetVoxel(localPos);
        }
        return -1.0f;
    }

    // ── API para la pantalla de depuración F3 (DebugF3, GDScript) ──────────────────────────

    /// <summary>Id del bioma (carpeta biome/) en una columna del mundo. Lo usa el F3.</summary>
    public string GetBiomeIdAt(int gx, int gz)
    {
        var reg = BiomeRegistry.Instance;
        if (_generator == null || reg == null) return "";
        int idx = _generator.BiomeIndexFor(_generator.SampleClimate(gx, gz));
        return idx >= 0 ? reg.GetByIndex(idx).Id : "";
    }

    /// <summary>Contadores del mundo para el F3: chunks cargados, colas, distancias, tipo de mundo...</summary>
    public Godot.Collections.Dictionary GetDebugStats()
    {
        var d = new Godot.Collections.Dictionary();
        d["loaded"] = _loadedChunks.Count;
        d["pending"] = _pendingChunks.Count;
        d["in_progress"] = _chunksInProgress.Count;
        d["finish_queue"] = _chunkFinishQueue.Count;
        d["pool"] = _chunkPool.Count;
        d["activation_queue"] = _activationQueue.Count;
        d["render_distance"] = RenderDistance;
        d["simulation_distance"] = SimulationDistance;
        d["world_type"] = _worldType;
        d["world_name"] = _currentWorldName ?? "";
        return d;
    }

    public static int Posmod(int a, int b)
    {
        int res = a % b;
        if ((res < 0 && b > 0) || (res > 0 && b < 0)) res += b;
        return res;
    }
}