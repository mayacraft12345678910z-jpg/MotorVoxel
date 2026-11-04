extends CharacterBody3D
class_name MotorVoxelPlayer
## CharacterBody3D.gd
##
## Le damos un class_name global ("MotorVoxelPlayer") únicamente para que
## otros scripts (como PlayerVisuals.gd) puedan acceder a la clase interna
## MinecraftBoxBuilder de abajo sin tener que hacer preload() con la ruta
## exacta de este archivo. El comportamiento de CharacterBody3D no cambia
## en nada por tener un class_name.

## ------------------------------------------------------------------
## CLASE INTERNA: MinecraftBoxBuilder
## ------------------------------------------------------------------
## Generador de mallas tipo "caja" (ArrayMesh) cuyas 6 caras están
## mapeadas con UV exactos al formato de atlas de skins de Minecraft
## (64x64), en vez de estirar la textura completa sobre cada cara como hace
## un BoxMesh normal.
##
## Cada parte del cuerpo (cabeza, torso, brazo, pierna) ocupa un bloque
## rectangular distinto dentro de la misma imagen de 64x64. Dentro de ese
## bloque, las 6 caras de la caja se despliegan en una "cruz":
##
##         [ TOP ][BOTTOM]
##  [RIGHT][FRONT][ LEFT ][BACK]
##
## Este layout es el estándar que usa Minecraft (y por lo tanto cualquier
## skin compatible), así que basta con conocer las dimensiones en píxeles
## de la parte (ancho, alto, profundidad) y la esquina superior-izquierda
## de su bloque en el atlas para calcular las 6 sub-regiones automáticamente.
##
## Se usa desde PlayerVisuals.gd así:
##   MotorVoxelPlayer.MinecraftBoxBuilder.build(width, height, depth, atlas_origin, anchor_top)
class MinecraftBoxBuilder:
	extends RefCounted

	const ATLAS_SIZE := Vector2(64.0, 64.0)

	## Cuántos píxeles de textura equivalen a 1 unidad de mundo (1 bloque).
	## Minecraft real usa 16 píxeles por bloque; lo mantenemos igual para
	## que las proporciones del modelo (cabeza, torso, brazos, piernas)
	## salgan correctas automáticamente sin tener que ajustar nada a mano.
	const PIXELS_PER_UNIT := 16.0

	## Construye una caja de (width, height, depth) unidades de mundo,
	## mapeada al bloque de atlas que empieza en atlas_origin (en píxeles),
	## con el layout estándar de Minecraft.
	##
	## anchor_top:
	##   true  -> la caja cuelga hacia ABAJO desde el origen local (0,0,0).
	##            Úsalo para torso, brazos y piernas, cuyo pivote representa
	##            el cuello/hombro/cadera (arriba de la pieza).
	##   false -> la caja se construye hacia ARRIBA desde el origen local.
	##            Úsalo para la cabeza, cuyo pivote representa la base del
	##            cuello (abajo de la pieza).
	static func build(width: float, height: float, depth: float,
			atlas_origin: Vector2, anchor_top: bool = true,
			atlas_size: Vector2 = ATLAS_SIZE) -> ArrayMesh:

		var hw := width * 0.5
		var hd := depth * 0.5
		var y0: float
		var y1: float
		if anchor_top:
			y0 = -height
			y1 = 0.0
		else:
			y0 = 0.0
			y1 = height

		# Dimensiones en píxeles de textura (para calcular los recortes UV).
		var pw := width * PIXELS_PER_UNIT
		var ph := height * PIXELS_PER_UNIT
		var pd := depth * PIXELS_PER_UNIT

		var ox := atlas_origin.x
		var oy := atlas_origin.y

		# Las 6 sub-regiones, siguiendo el layout estándar de Mojang:
		# fila superior = top/bottom; fila inferior = right/front/left/back.
		var rect_top    := Rect2(ox + pd, oy, pw, pd)
		var rect_bottom := Rect2(ox + pd + pw, oy, pw, pd)
		var rect_right  := Rect2(ox, oy + pd, pd, ph)
		var rect_front  := Rect2(ox + pd, oy + pd, pw, ph)
		var rect_left   := Rect2(ox + pd + pw, oy + pd, pd, ph)
		var rect_back   := Rect2(ox + pd + pw + pd, oy + pd, pw, ph)

		var st := SurfaceTool.new()
		st.begin(Mesh.PRIMITIVE_TRIANGLES)

		# Frente (+Z, hacia la cámara por defecto en Godot).
		_add_face(st, Vector3(-hw, y1, hd), Vector3(hw, y1, hd),
				Vector3(hw, y0, hd), Vector3(-hw, y0, hd), rect_front, atlas_size)
		# Espalda (-Z).
		_add_face(st, Vector3(hw, y1, -hd), Vector3(-hw, y1, -hd),
				Vector3(-hw, y0, -hd), Vector3(hw, y0, -hd), rect_back, atlas_size)
		# Lado izquierdo del personaje (+X cuando mira hacia +Z).
		_add_face(st, Vector3(hw, y1, hd), Vector3(hw, y1, -hd),
				Vector3(hw, y0, -hd), Vector3(hw, y0, hd), rect_left, atlas_size)
		# Lado derecho del personaje (-X).
		_add_face(st, Vector3(-hw, y1, -hd), Vector3(-hw, y1, hd),
				Vector3(-hw, y0, hd), Vector3(-hw, y0, -hd), rect_right, atlas_size)
		# Arriba (+Y).
		_add_face(st, Vector3(-hw, y1, -hd), Vector3(hw, y1, -hd),
				Vector3(hw, y1, hd), Vector3(-hw, y1, hd), rect_top, atlas_size)
		# Abajo (-Y).
		_add_face(st, Vector3(-hw, y0, hd), Vector3(hw, y0, hd),
				Vector3(hw, y0, -hd), Vector3(-hw, y0, -hd), rect_bottom, atlas_size)

		st.generate_normals()
		st.index()
		return st.commit()

	## Agrega un quad (2 triángulos) al SurfaceTool, con sus 4 vértices
	## ordenados en sentido horario vistos desde afuera (a = arriba-izq,
	## b = arriba-der, c = abajo-der, d = abajo-izq) y su UV calculado a
	## partir de un rect en píxeles dentro del atlas completo.
	static func _add_face(st: SurfaceTool, a: Vector3, b: Vector3, c: Vector3, d: Vector3,
			px_rect: Rect2, atlas_size: Vector2) -> void:
		var u0 := px_rect.position.x / atlas_size.x
		var v0 := px_rect.position.y / atlas_size.y
		var u1 := (px_rect.position.x + px_rect.size.x) / atlas_size.x
		var v1 := (px_rect.position.y + px_rect.size.y) / atlas_size.y

		st.set_uv(Vector2(u0, v0)); st.add_vertex(a)
		st.set_uv(Vector2(u1, v0)); st.add_vertex(b)
		st.set_uv(Vector2(u1, v1)); st.add_vertex(c)

		st.set_uv(Vector2(u0, v0)); st.add_vertex(a)
		st.set_uv(Vector2(u1, v1)); st.add_vertex(c)
		st.set_uv(Vector2(u0, v1)); st.add_vertex(d)

## ------------------------------------------------------------------
## SCRIPT PRINCIPAL: movimiento, cámaras, interacción
## ------------------------------------------------------------------

@export_group("Movimiento")
@export var speed: float = 6.0
@export var fly_speed: float = 17.0
@export var jump_velocity: float = 6.5
@export var sensitivity: float = 0.003

@export_group("Interacción")
@export var reach_distance: float = 6.0
@export var break_speed: float = 0.12 
@export var place_speed: float = 0.15 

@export var Gravedad: float = 5 

var break_timer = 0.0
var place_timer = 0.0

## ── Inventario (reemplaza a block_types/selected_index) ──────────────────────
## Modelo único: la hotbar son los últimos 9 slots de `inventory`.
var inventory: Inventory = Inventory.new()
var _creativo: bool = true
## Clic central pendiente: el raycast se hace en _physics_process (el espacio de física
## está bloqueado fuera de ahí porque 3d/run_on_separate_thread=true).
var _elegir_bloque_pendiente: bool = false
var is_flying = false
var last_jump_time = 0.0
var double_tap_time = 0.3
var gravity = ProjectSettings.get_setting("physics/3d/default_gravity") + Gravedad

# FIX líneas 27-32 — Se elimina @onready var camera estática y se añaden las exportaciones para el sistema multicámara F5
@export_group("Inventario / HUD")
@export var hotbar_ui: HotbarUI
@export var inventario_root: InventarioRoot
@export var stats: PlayerStats

@export_group("Cámaras")
@export var camera_1: Camera3D
@export var camera_2: Camera3D
@export var camera_3: Camera3D

## Índice (dentro de cameras[]) que se considera "primera persona". Por
## defecto camera_1 (índice 0). Cuando la cámara activa sea esa, el modelo
## del jugador se oculta (para no ver tu propia cabeza/cuerpo bloqueando la
## vista); con cualquier otra cámara (tercera persona) se muestra.
@export var first_person_camera_index: int = 0

## Nodo opcional para el torso si vive FUERA de Visuals (como un hermano
## directo de este CharacterBody3D). Si tu torso ya está dentro de Visuals,
## déjalo vacío: visuals.visible ya lo cubre.
@export var external_body_mesh: Node3D

## Altura extra (en unidades de MUNDO, hacia arriba) que se suma al punto de
## la cabeza para mover el pivote de la órbita. En tu escena visuals.head ya
## apunta al centro de la malla de la cabeza, así que 0.0 es lo correcto;
## súbelo o bájalo solo si quieres girar alrededor de otro punto.
@export_group("Cámaras 3ª persona (órbita)")
@export var head_pivot_height_offset: float = 0.0

## Punto de pivote (en espacio local del jugador) que se usa SOLO si no se
## encuentra la cabeza en Visuals (visuals.head).
@export var head_pivot_fallback: Vector3 = Vector3(0.0, 0.6, 0.0)

var cameras: Array[Camera3D] = []
var current_camera_index: int = 0
var camera: Camera3D

## Ángulo vertical (pitch) de la mirada del jugador, en radianes. Es la única
## fuente de verdad: la cámara de 1ª persona lo usa como rotation.x, las de
## 3ª persona lo usan para orbitar alrededor de la cabeza, y PlayerVisuals
## lo recibe como look_pitch para inclinar la cabeza del modelo.
var _pitch: float = 0.0

## Por cada cámara de 3ª persona (clave = índice en cameras[]) se guarda su
## posición y orientación INICIAL relativas al pivote de la cabeza, tal como
## las dejaste en el editor. Luego solo se rotan rígidamente con el pitch.
var _orbit_data: Dictionary = {}

# ── NUEVO: F3+G (bordes de chunk, igual que Minecraft) ──────────────────────
var _chunk_border_debug: Node3D
var _f3_held := false

# ── NUEVO: contorno negro del bloque al que apunta la cámara ────────────────
var _block_outline: MeshInstance3D

# ── NUEVO: menú de pausa (Esc) ───────────────────────────────────────────
## Escena del menú de pausa. Se instancia como hija de la raíz del árbol
## (no de este jugador) para que quede por encima de todo, incluida la
## cámara/UI del jugador.
const MENU_PAUSA_SCENE := preload("res://script/gdscript/menu_pausa/menu_pausa.tscn")
var _menu_pausa: CanvasLayer = null

@onready var visuals = get_node_or_null("Visuals")

func _ready():
	# NUEVO: World.cs ahora es ProcessMode.Always (para que la carga/descarga
	# de chunks siga funcionando con el menú de pausa abierto). Como este
	# jugador es hijo de World, con Inherit (el valor por defecto) heredaría
	# ese Always y seguiría moviéndose/procesando input con el juego en
	# pausa. Se fija explícito en Pausable para que SÍ quede congelado.
	process_mode = Node.PROCESS_MODE_PAUSABLE

	Input.mouse_mode = Input.MOUSE_MODE_CAPTURED

	# ── Skin persistida: aplica sobre Visuals la skin que el jugador eligió
	# en el menú de Skins (o la de Steve por defecto si nunca eligió otra).
	if visuals and visuals.has_method("load_skin"):
		visuals.load_skin(SkinManager.get_current_skin_path())

	# # FIX línea 170 — Cambiado a 0 grados absolutos porque en un mundo voxel no existen las rampas; cualquier contacto inclinado es una pared o borde suelto
	floor_max_angle = deg_to_rad(0)
	
	# # FIX línea 173 — Configurado en 0.0 para evitar que las físicas fuercen el reenganche artificial del jugador al intentar caer desde las aristas
	floor_snap_length = 0.0
	
	# # FIX línea 176 — Reducido drásticamente para prevenir que la caja de colisión flote o se atranque matemáticamente en los vértices de 90 grados
	safe_margin = 0.001
	
	# ADD líneas 42-49 — Inicialización de las cámaras
	if camera_1: cameras.append(camera_1)
	if camera_2: cameras.append(camera_2)
	if camera_3: cameras.append(camera_3)
	
	if cameras.size() > 0:
		camera = cameras[0]
		_setup_orbit_cameras()
		update_cameras_state()
	else:
		camera = get_node_or_null("Camera3D")

	# NUEVO: se une al grupo "player" para que ConfiguracionJuego (autoload)
	# lo encuentre y le aplique sensibilidad/FOV guardados, sin necesitar
	# una referencia exportada desde ese autoload hacia acá.
	add_to_group("player")
	sensitivity = ConfiguracionJuego.sensibilidad_mouse
	aplicar_fov(ConfiguracionJuego.fov)
	
	_iniciar_inventario()

	# ── NUEVO: F3+G (bordes de chunk, igual que Minecraft) ──────────────────
	# Se crea el nodo de depuración directo por código, no hace falta
	# agregarlo a mano en ninguna escena.
	_chunk_border_debug = preload("res://script/gdscript/debug/ChunkBorderDebug.gd").new()
	get_tree().root.add_child.call_deferred(_chunk_border_debug)

	# ── NUEVO: contorno negro del bloque apuntado ────────────────────────
	_create_block_outline()

## Crea, una sola vez, la malla de aristas (cubo unitario 0..1) que se usa
## para resaltar el bloque al que apunta la cámara. Se reposiciona/reescala
## cada frame en _update_block_outline(); nunca se reconstruye la geometría.
func _create_block_outline() -> void:
	var verts := PackedVector3Array([
		Vector3(0, 0, 0), Vector3(1, 0, 0), Vector3(1, 0, 1), Vector3(0, 0, 1),
		Vector3(0, 1, 0), Vector3(1, 1, 0), Vector3(1, 1, 1), Vector3(0, 1, 1),
	])
	# 12 aristas del cubo como pares de índices (PRIMITIVE_LINES).
	var indices := PackedInt32Array([
		0, 1, 1, 2, 2, 3, 3, 0,   # cara inferior
		4, 5, 5, 6, 6, 7, 7, 4,   # cara superior
		0, 4, 1, 5, 2, 6, 3, 7,   # verticales
	])

	var arrays: Array = []
	arrays.resize(Mesh.ARRAY_MAX)
	arrays[Mesh.ARRAY_VERTEX] = verts
	arrays[Mesh.ARRAY_INDEX] = indices

	var mesh := ArrayMesh.new()
	mesh.add_surface_from_arrays(Mesh.PRIMITIVE_LINES, arrays)

	var mat := StandardMaterial3D.new()
	mat.shading_mode = BaseMaterial3D.SHADING_MODE_UNSHADED
	mat.albedo_color = Color.BLACK
	mesh.surface_set_material(0, mat)

	_block_outline = MeshInstance3D.new()
	_block_outline.mesh = mesh
	_block_outline.visible = false
	# Se cuelga de la raíz del árbol (no del jugador): su transform tiene que
	# quedar en espacio de MUNDO, no relativo a un nodo que rota/se mueve.
	get_tree().root.add_child.call_deferred(_block_outline)

func _unhandled_input(event):
	# ── NUEVO: menú de pausa (Esc) ────────────────────────────────────────
	# "ui_cancel" es la acción incorporada de Godot mapeada a Escape por
	# defecto. Se maneja PRIMERO y sin condiciones (ni siquiera necesita
	# `camera`) porque tiene que poder abrirse en cualquier momento.
	if event.is_action_pressed("ui_cancel"):
		_abrir_menu_pausa()
		get_viewport().set_input_as_handled()
		return

	# ADD líneas 76-78 — Detección de la tecla F5 para ciclar cámaras
	# CAMBIO: usa la acción "f5" del InputMap (reasignable desde Opciones >
	# Controles) en vez del keycode fijo.
	if event.is_action_pressed("f5"):
		cycle_camera()

	# ── NUEVO: F3+G (bordes de chunk, igual que Minecraft) ──────────────────
	# Igual que en el juego original: hay que mantener F3 presionada y, sin
	# soltarla, presionar G para activar/desactivar el contorno del chunk.
	# CAMBIO: usa las acciones "f3"/"G" en vez de los keycodes fijos.
	if event.is_action_pressed("f3"):
		_f3_held = true
	elif event.is_action_released("f3"):
		_f3_held = false

	if event.is_action_pressed("G") and _f3_held:
		if _chunk_border_debug and _chunk_border_debug.has_method("toggle"):
			_chunk_border_debug.toggle()

	if camera == null: return
	
	# Alternar pantalla completa — CAMBIO: usa la acción "pantalla_completa"
	# (por defecto F11) en vez del keycode fijo.
	if event.is_action_pressed("pantalla_completa"):
		toggle_fullscreen()
	
	# Con el inventario abierto: cámara fija y nada de hotbar/construir.
	if _ui_abierta():
		return

	if event is InputEventMouseMotion:
		rotate_y(-event.relative.x * sensitivity)
		_pitch = clamp(_pitch - event.relative.y * sensitivity, deg_to_rad(-89), deg_to_rad(89))
		_apply_camera_pitch()

	# ── Hotbar: rueda (abajo = +1 derecha, arriba = -1, con vuelta 9↔1) ────
	if event is InputEventMouseButton and event.pressed:
		if event.button_index == MOUSE_BUTTON_WHEEL_DOWN:
			inventory.scroll(1)
		elif event.button_index == MOUSE_BUTTON_WHEEL_UP:
			inventory.scroll(-1)
	for i in 9:
		if event.is_action_pressed("hotbar_%d" % (i + 1)):
			inventory.set_selected(i)
	# Q suelta 1; Ctrl+Q la pila completa (TODO ItemEntity: por ahora se descarta)
	if event.is_action_pressed("soltar_item"):
		inventory.drop_selected(Input.is_key_pressed(KEY_CTRL))
	# F intercambia con la mano secundaria
	if event.is_action_pressed("intercambiar_offhand"):
		inventory.swap_selected_with_offhand()
	# Clic central (solo Creativo): elegir bloque
	if event.is_action_pressed("elegir_bloque"):
		_elegir_bloque_pendiente = true

# ADD líneas 101-110 — Lógica para alternar las cámaras activas
func cycle_camera():
	if cameras.size() < 2: return
	current_camera_index = (current_camera_index + 1) % cameras.size()
	camera = cameras[current_camera_index]
	update_cameras_state()

func update_cameras_state():
	for i in range(cameras.size()):
		cameras[i].current = (i == current_camera_index)
	_update_view_visuals()
	_apply_camera_pitch()

## Punto de pivote (posición GLOBAL) alrededor del que orbitan las cámaras
## de 2ª/3ª persona: la cabeza del modelo (visuals.head). Solo LEE Visuals;
## no lo modifica.
func _get_head_pivot_global() -> Vector3:
	if visuals and "head" in visuals and visuals.head:
		var head_node: Node3D = visuals.head
		return head_node.global_position + Vector3.UP * head_pivot_height_offset
	return to_global(head_pivot_fallback)

## Marco de referencia del jugador SIN escala. Tu CharacterBody3D tiene
## escala no uniforme (1.258, 1.584, 1.258); rotar dentro de un espacio así
## deformaría la cámara. Con este marco (solo posición + rotación/yaw) la
## órbita es una rotación pura, en unidades de mundo.
func _get_orbit_frame() -> Transform3D:
	return Transform3D(global_transform.basis.orthonormalized(), global_transform.origin)

## Cámara de primera persona = camera_1. (No se usa first_person_camera_index
## para esto: ese índice solo decide si se oculta el modelo.)
func _is_first_person_cam(cam: Camera3D) -> bool:
	return cam == camera_1 or cam == null

## Guarda, para cada cámara de 2ª/3ª persona, dónde está y hacia dónde mira
## respecto al pivote de la cabeza (según la dejaste en el editor). La cámara
## de primera persona NO se toca.
func _setup_orbit_cameras() -> void:
	_orbit_data.clear()
	var frame: Transform3D = _get_orbit_frame()
	var inv: Transform3D = frame.affine_inverse()
	var pivot_local: Vector3 = inv * _get_head_pivot_global()
	for i in range(cameras.size()):
		var cam: Camera3D = cameras[i]
		if _is_first_person_cam(cam):
			continue
		_orbit_data[i] = {
			"offset": (inv * cam.global_position) - pivot_local,
			"basis": frame.basis.inverse() * cam.global_transform.basis.orthonormalized(),
		}
		# top_level: la cámara deja de heredar la transformación de su nodo
		# padre (y su escala), así su posición/rotación las decide SOLO
		# _apply_camera_pitch(). Conserva su transform global al activarlo.
		cam.top_level = true

## Las cámaras de 2ª/3ª persona ya no siguen a su padre (top_level), así que
## se reposicionan cada frame para acompañar al jugador al caminar/saltar/girar.
func _process(_delta: float) -> void:
	if not _orbit_data.is_empty():
		_apply_camera_pitch()

## Aplica el pitch actual a todas las cámaras:
## - 1ª persona (camera_1): rota sobre sí misma, como siempre.
## - 2ª/3ª persona: orbita RÍGIDAMENTE alrededor de la cabeza, así siempre
##   miran al mismo punto y se mueven en arco al subir/bajar el mouse.
func _apply_camera_pitch() -> void:
	if cameras.is_empty():
		if camera:
			camera.rotation.x = _pitch
		return

	var pitch_basis := Basis(Vector3.RIGHT, _pitch)
	var frame: Transform3D = _get_orbit_frame()
	var pivot_local: Vector3 = frame.affine_inverse() * _get_head_pivot_global()

	for i in range(cameras.size()):
		var cam: Camera3D = cameras[i]
		if cam == null:
			continue
		if _is_first_person_cam(cam) or not _orbit_data.has(i):
			cam.rotation.x = _pitch
			continue
		var data: Dictionary = _orbit_data[i]
		var local_pos: Vector3 = pivot_local + pitch_basis * data["offset"]
		var local_basis: Basis = pitch_basis * data["basis"]
		cam.global_transform = frame * Transform3D(local_basis, local_pos)

## Oculta el modelo del jugador cuando la cámara activa es la de primera
## persona (para no ver tu propia cabeza/torso tapando la pantalla), y lo
## muestra con cualquier otra cámara (tercera persona).
func _update_view_visuals() -> void:
	var is_first_person: bool = current_camera_index == first_person_camera_index

	if visuals:
		visuals.visible = not is_first_person

	if external_body_mesh:
		external_body_mesh.visible = not is_first_person

## Instancia el menú de pausa por encima de todo (como hija de la escena
## actual, no de este jugador puntual) si todavía no hay uno abierto. El
## propio menú se encarga de pausar el árbol (get_tree().paused = true) y
## de liberar el mouse; acá solo hace falta crearlo y escuchar cuándo se
## cierra para volver a capturar el mouse.
##
## CORREGIDO: antes se agregaba como hijo directo de get_tree().root. El
## problema es que get_tree().change_scene_to_file() (usado por "Guardar
## y salir al menú principal") solo reemplaza la ESCENA ACTUAL, no otros
## hijos sueltos de root — así que el menú de pausa quedaba "flotando"
## para siempre encima del menú principal después de salir, sin nunca
## liberarse. Al colgarlo de get_tree().current_scene en cambio, se libera
## solo junto con el resto de la escena cuando esta se cambia.
##
## NUEVO: aprovechamos el momento justo antes de abrir el menú —cuando la
## pantalla todavía muestra el juego "limpio", sin el overlay oscuro de
## pausa dibujado encima— para hacer un autoguardado del mundo y tomar la
## miniatura (el "world icon"). Si lo hiciéramos DESPUÉS de abrir el menú,
## la miniatura saldría con el fondo oscuro y los botones de pausa
## dibujados arriba, como pasa en el bug de Bedrock (MCPE-177770).
func _abrir_menu_pausa() -> void:
	if _menu_pausa != null and is_instance_valid(_menu_pausa):
		return
	_guardar_inventario()

	# Autoguardado + miniatura del mundo al entrar a pausa (todavía con la
	# pantalla "limpia"). Si no hay persistencia activa o no hay un mundo
	# cargado por WorldManager, estas llamadas no hacen nada.
	#
	# OJO: esto tiene que cubrir TODO lo que el juego guarda al cerrar la
	# ventana (mismo criterio que _Notification/NOTIFICATION_WM_CLOSE_REQUEST
	# en World.cs / WorldManager.gd / MinecraftDayNight.gd), no solo la
	# posición del jugador. Por eso son 3 llamadas, una por cada sistema:
	#   1. World          -> posición del jugador + chunks/bloques editados
	#   2. WorldManager    -> miniatura ("world icon") del mundo
	#   3. World/DayNight -> en qué momento del día/noche quedó el mundo
	var world := get_node_or_null("/root/World")
	if world and world.has_method("GuardarMundoAhora"):
		world.GuardarMundoAhora()
	var wm := get_node_or_null("/root/WorldManager")
	if wm:
		wm.guardar_miniatura_mundo_actual()
	var ciclo_dia_noche := get_node_or_null("/root/World/DayNight")
	if ciclo_dia_noche and ciclo_dia_noche.has_method("guardar_estado_dia_noche"):
		ciclo_dia_noche.guardar_estado_dia_noche()

	_menu_pausa = MENU_PAUSA_SCENE.instantiate()
	var padre: Node = get_tree().current_scene
	if padre == null:
		padre = get_tree().root
	padre.add_child(_menu_pausa)
	_menu_pausa.cerrado.connect(_on_menu_pausa_cerrado, CONNECT_ONE_SHOT)

## Se llama cuando el menú de pausa se cierra (botón "Volver al juego" o
## Esc de nuevo). El menú ya se encarga de despausar el árbol; acá solo
## queda recapturar el mouse para seguir jugando en primera persona.
func _on_menu_pausa_cerrado() -> void:
	_menu_pausa = null
	Input.mouse_mode = Input.MOUSE_MODE_CAPTURED

func toggle_fullscreen():
	# Pasa por ConfiguracionJuego en vez de tocar DisplayServer directo, así
	# queda guardado y el checkbox "Pantalla completa" del menú de Video
	# refleja lo mismo la próxima vez que se abra.
	ConfiguracionJuego.set_pantalla_completa(not ConfiguracionJuego.pantalla_completa)


## Aplica el campo de visión guardado a todas las cámaras del jugador (las
## 3 del sistema multicámara F5, no solo la que está activa ahora mismo).
func aplicar_fov(valor: float) -> void:
	for cam in cameras:
		if cam:
			cam.fov = valor
	if camera and not cameras.has(camera):
		camera.fov = valor

func _physics_process(delta):
	if _elegir_bloque_pendiente:
		_elegir_bloque_pendiente = false
		_elegir_bloque()
	# CAMBIO: "romper_bloque"/"colocar_bloque" son acciones del InputMap
	# (por defecto clic izq./der.) en vez de MOUSE_BUTTON fijo, así se
	# pueden reasignar desde Opciones > Controles.
	if (Input.is_action_pressed("romper_bloque") and not _ui_abierta()):
		break_timer += delta
		if break_timer >= break_speed:
			interact_with_world(false)
			break_timer = 0.0
	else:
		break_timer = break_speed

	if (Input.is_action_pressed("colocar_bloque") and not _ui_abierta()):
		place_timer += delta
		if place_timer >= place_speed:
			interact_with_world(true)
			place_timer = 0.0
	else:
		place_timer = place_speed

	if Input.is_action_just_pressed("ui_accept") and not _ui_abierta():
		var current_time = Time.get_ticks_msec() / 1000.0
		if current_time - last_jump_time < double_tap_time:
			is_flying = !is_flying
			velocity.y = 0
		last_jump_time = current_time

	if is_flying:
		handle_fly_movement(delta)
	else:
		handle_ground_movement(delta)
	move_and_slide()

	_update_visuals()
	_update_block_outline()

func _update_visuals() -> void:
	if visuals == null:
		return

	# Velocidad horizontal (caminar/correr) y vertical (volar hacia
	# arriba/abajo). Usamos la mayor de las dos para que el balanceo de
	# brazos y piernas reaccione tanto al caminar como al volar.
	var horizontal_speed: float = Vector2(velocity.x, velocity.z).length()
	var vertical_speed: float = abs(velocity.y) if is_flying else 0.0
	var raw_speed: float = max(horizontal_speed, vertical_speed)

	# Normalizamos contra la velocidad máxima correspondiente al modo
	# actual, para que PlayerVisuals reciba siempre un valor proporcional
	# (0 = quieto, 1 = velocidad máxima), sin importar si speed/fly_speed
	# son muy distintos entre sí.
	var max_speed: float = fly_speed if is_flying else speed
	visuals.current_speed = clamp(raw_speed / max_speed, 0.0, 1.0)

	# Minando: mientras se mantiene presionado el clic izquierdo sobre el
	# mundo (rompiendo bloques), el brazo derecho hace el golpeo procedural.
	visuals.is_mining = (Input.is_action_pressed("romper_bloque") and not _ui_abierta())

	# Salto: en el aire (no en el suelo) y sin estar volando, activamos la
	# pose de salto (piernas recogidas, brazos abiertos para equilibrio).
	visuals.is_airborne = (not is_on_floor()) and not is_flying

	# Mirada vertical: la cabeza del modelo sigue el pitch de la cámara
	# activa, así se ve hacia dónde está mirando el jugador (relevante en
	# tercera persona y para clientes remotos en multijugador).
	# Se usa _pitch (no camera.rotation.x) porque en 3ª persona la cámara
	# orbita y su rotation.x local ya no equivale al pitch del jugador.
	visuals.look_pitch = _pitch

func handle_fly_movement(_delta):
	var input_dir = Vector2.ZERO if _ui_abierta() else Input.get_vector("ui_left", "ui_right", "ui_up", "ui_down")
	var direction = (transform.basis * Vector3(input_dir.x, 0, input_dir.y)).normalized()
	velocity.x = direction.x * fly_speed
	velocity.z = direction.z * fly_speed
	# CAMBIO: "agachar" es una acción del InputMap (por defecto Shift) en
	# vez de KEY_SHIFT fijo, así se puede reasignar desde Opciones > Controles.
	if not _ui_abierta() and Input.is_action_pressed("ui_accept"): velocity.y = fly_speed
	elif not _ui_abierta() and Input.is_action_pressed("agachar"): velocity.y = -fly_speed
	else: velocity.y = 0

func handle_ground_movement(delta):
	if not is_on_floor(): velocity.y -= gravity * delta
	if Input.is_action_just_pressed("ui_accept") and is_on_floor() and not _ui_abierta(): velocity.y = jump_velocity
	var input_dir = Vector2.ZERO if _ui_abierta() else Input.get_vector("ui_left", "ui_right", "ui_up", "ui_down")
	var direction = (transform.basis * Vector3(input_dir.x, 0, input_dir.y)).normalized()
	if direction:
		velocity.x = direction.x * speed
		velocity.z = direction.z * speed
	else:
		velocity.x = move_toward(velocity.x, 0, speed)
		velocity.z = move_toward(velocity.z, 0, speed)

## NUEVO: cada paso de física (no en _process/idle) lanza el mismo rayo que
## interact_with_world para saber qué bloque está mirando el jugador, y
## mueve/escala el contorno negro para que calce con su caja real (usa
## BoxMin/BoxMax de BlockRegistry, así que también funciona con losas u
## otros bloques con geometría reducida, no solo cubos completos).
##
## CORREGIDO: esto vivía en _process(). direct_space_state solo se puede
## consultar de forma segura durante el paso de física — fuera de ahí (con
## el espacio bloqueado mientras corre el hilo de físicas) Godot devuelve
## "Space state is inaccesible right now" y de paso get_world_3d().direct_
## space_state termina siendo null, lo que rompía intersect_ray().
func _update_block_outline() -> void:
	if _block_outline == null or camera == null:
		return

	var space_state = get_world_3d().direct_space_state
	var center = get_viewport().get_visible_rect().size / 2
	var from = camera.project_ray_origin(center)
	var to = from + camera.project_ray_normal(center) * reach_distance
	var query = PhysicsRayQueryParameters3D.create(from, to)
	query.exclude = [get_rid()]

	var result = space_state.intersect_ray(query)
	if not result:
		_block_outline.visible = false
		return

	# Misma corrección que en interact_with_world: empujar el punto de
	# impacto un poquito hacia ADENTRO del bloque (contra la normal) antes
	# de redondear, para no "resbalar" a la celda vecina por errores de
	# precisión justo en el borde exacto de la celda.
	var normal_i = Vector3i(round(result.normal.x), round(result.normal.y), round(result.normal.z))
	var probe = result.position - Vector3(normal_i) * 0.01
	var clicked_cell = Vector3i(floor(probe.x), floor(probe.y), floor(probe.z))

	var world = get_parent()
	if world == null or not world.has_method("GetBlockGlobal"):
		_block_outline.visible = false
		return

	var block_id: float = world.GetBlockGlobal(clicked_cell)
	if block_id <= 0.0:  # aire, o chunk todavía no cargado: nada que resaltar
		_block_outline.visible = false
		return

	var box_min: Vector3 = BlockRegistry.GetBoxMin(block_id)
	var box_max: Vector3 = BlockRegistry.GetBoxMax(block_id)

	# Margen minúsculo hacia AFUERA de la caja real del bloque, para que el
	# contorno no parpadee (z-fighting) pegado justo contra su cara. No se
	# usa "no_depth_test" para esto porque eso dibujaría el contorno encima
	# de todo, incluso a través de otros bloques que lo tapen.
	const MARGIN = 0.002
	var size: Vector3 = (box_max - box_min) + Vector3.ONE * (MARGIN * 2.0)
	var origin: Vector3 = Vector3(clicked_cell) + box_min - Vector3.ONE * MARGIN

	_block_outline.global_transform = Transform3D(Basis().scaled(size), origin)
	_block_outline.visible = true

func interact_with_world(is_placing: bool):
	if camera == null or inventory == null: return
	# Lo que se coloca sale del slot seleccionado de la hotbar (solo ítems-bloque).
	var type_to_set = 0.0
	if is_placing:
		var _mano := inventory.get_selected_stack()
		if _mano.is_empty(): return
		type_to_set = ItemRegistry.item_to_block_id(_mano.item_id)
		if type_to_set < 0.0: return
	var space_state = get_world_3d().direct_space_state
	var center = get_viewport().get_visible_rect().size / 2
	var from = camera.project_ray_origin(center)
	var to = from + camera.project_ray_normal(center) * reach_distance
	var query = PhysicsRayQueryParameters3D.create(from, to)
	query.exclude = [get_rid()]
	
	var result = space_state.intersect_ray(query)
	if result:
		# ── CORREGIDO: cálculo de celda por desplazamiento ENTERO ───────────
		# Antes: se empujaba el punto de impacto 0.1 en la dirección de la
		# normal y se redondeaba. Eso solo funciona si la cara clickeada está
		# en el borde exacto de la celda (y=1.0, un bloque completo). Contra
		# una LOSA, cuya superficie expuesta está a media altura (y=0.5), ese
		# empujón de 0.1 no alcanza a salir de la celda -> el bloque nuevo
		# terminaba sobrescribiendo la losa en vez de apilarse encima.
		#
		# Ahora: se calcula la celda REALMENTE clickeada empujando el punto de
		# impacto un poquito hacia ADENTRO del bloque (contra la normal, no a
		# favor), y la celda de colocación es esa celda + 1 entero en la
		# dirección de la normal. Esto funciona sin importar la altura real
		# del bloque clickeado (cubo completo, losa, Lodo, Tierra Aplastada, etc.).
		var normal_i = Vector3i(round(result.normal.x), round(result.normal.y), round(result.normal.z))
		var probe = result.position - Vector3(normal_i) * 0.01
		var clicked_cell = Vector3i(floor(probe.x), floor(probe.y), floor(probe.z))
		var world = get_parent()

		# ── CORREGIDO: clic en la superficie INTERIOR de una losa ───────────
		# La regla "celda clickeada + 1 en la normal" (arriba) es correcta
		# SOLO cuando la cara clickeada está en el borde real de la celda
		# (y=1.0 o y=0.0, un bloque completo). Pero al clickear la superficie
		# EXPUESTA de una losa de abajo (que está a media altura, y=0.5, no
		# en el borde), eso significaba saltar a la celda de encima en vez de
		# intentar llenar la mitad que falta en ESA MISMA celda — por eso
		# combinar/mezclar losas desde arriba quedaba flotando mal.
		#
		# Ahora: si vas a colocar una LOSA y el clic fue en una cara
		# horizontal (arriba/abajo) que NO está en el borde real de la celda
		# (osea, el bloque que clickeaste ya es una losa/geometría reducida),
		# la celda objetivo es la MISMA celda clickeada, para intentar llenar
		# la mitad que le falta.
		var block_v3 = clicked_cell + normal_i
		if is_placing and BlockRegistry.IsBottomSlab(type_to_set) and normal_i.y != 0 and world and world.has_method("GetBlockGlobal"):
			var clicked_hit_local_y = result.position.y - float(clicked_cell.y)
			var hit_is_interior = clicked_hit_local_y > 0.15 and clicked_hit_local_y < 0.85
			if hit_is_interior:
				block_v3 = clicked_cell

		if not is_placing:
			block_v3 = clicked_cell

		# NUEVO: pasto, helechos, arbusto seco, hierba marina... se REEMPLAZAN al
		# colocar un bloque apuntandolos (igual que Minecraft), en vez de poner
		# el bloque al lado.
		if is_placing and world and world.has_method("GetBlockGlobal") and BlockRegistry.IsReplaceablePlant(world.GetBlockGlobal(clicked_cell)):
			block_v3 = clicked_cell

		# Constantes de codificación de LOSAS MEZCLADAS — deben coincidir
		# EXACTO con MixedSlabBase/MixedSlabScale en Chunk.cs.
		const MIXED_SLAB_BASE = 1000000.0
		const MIXED_SLAB_SCALE = 10000.0

		# ── orientación arriba/abajo + combinar/mezclar LOSAS ───────────────
		# No se crea NINGÚN bloque/JSON nuevo para ninguna variante: se usa
		# SIEMPRE la misma losa de siempre y solo se codifica un decimal (o,
		# para mezclas de 2 materiales, un id "gigante" reservado) en el
		# valor que se guarda en la celda — igual que el agua ya usa su
		# decimal para el nivel. Chunk.cs interpreta ese valor para dibujar
		# la geometría/textura correcta.
		if is_placing and BlockRegistry.IsBottomSlab(type_to_set) and world and world.has_method("GetBlockGlobal"):
			var base_id = type_to_set
			var existing = world.GetBlockGlobal(block_v3)
			var existing_is_air = existing <= 0.0
			var existing_is_mixed = existing >= MIXED_SLAB_BASE

			# ¿Qué mitad querrías poner según dónde hiciste clic? Se calcula
			# siempre (hace falta tanto para celda vacía como para intentar
			# combinar/mezclar con lo que ya hay).
			var wants_top := false
			if block_v3 == clicked_cell and normal_i.y != 0:
				# Caso especial: clickeaste la superficie INTERIOR de una losa
				# ya puesta (ver arriba) -> quieres la mitad que le FALTA a
				# esa losa, que es la opuesta a la cara que clickeaste.
				# Clickear su cara de arriba (normal.y>0) = existente está
				# abajo -> quieres arriba. Clickear su cara de abajo
				# (normal.y<0, techo) = existente está arriba -> quieres abajo.
				wants_top = normal_i.y > 0
			elif result.normal.y < -0.5:
				# Clic en la cara de abajo de un bloque (techo) -> losa arriba.
				wants_top = true
			elif absf(result.normal.y) < 0.5:
				# Clic en una cara lateral -> depende de la mitad exacta del
				# punto de impacto dentro de la celda clickeada.
				var hit_local_y = result.position.y - floor(result.position.y)
				wants_top = hit_local_y > 0.5
			# (Clic en la cara de arriba de un bloque COMPLETO: normal.y > 0.5
			# -> se queda abajo, que es el comportamiento normal.)

			if existing_is_air:
				type_to_set = base_id + (0.5 if wants_top else 0.0)

			elif existing_is_mixed:
				# Ya hay 2 losas de materiales distintos en esa celda -> ya
				# está llena, no cabe una tercera.
				return

			else:
				var existing_base = floor(existing + 0.001)
				var existing_frac = existing - existing_base
				var existing_is_combined_same = existing_frac > 0.65 and existing_frac < 0.85
				var existing_is_top = existing_frac > 0.35 and existing_frac < 0.65
				# (si no es ninguno de los 2, la existente está "abajo")

				if existing_is_combined_same:
					# Ya son 2 losas del MISMO material combinadas -> llena.
					return

				if is_equal_approx(existing_base, base_id):
					# Mismo material que el que ya está puesto -> combinar:
					# se ve como bloque completo, pero siguen siendo 2 losas.
					type_to_set = base_id + 0.75
				else:
					# Material DISTINTO al que ya hay en la celda. Se pueden
					# MEZCLAR (2 materiales, cada uno en su mitad), pero SOLO
					# si apuntas a la mitad OPUESTA a la que ya está ocupada
					# — igual que en Minecraft, no puedes meter una losa
					# donde ya hay otra en la misma mitad.
					if existing_is_top and not wants_top:
						# Ya hay una arriba (existing_base) -> pones la nueva abajo.
						type_to_set = MIXED_SLAB_BASE + floor(base_id) * MIXED_SLAB_SCALE + floor(existing_base)
					elif (not existing_is_top) and wants_top:
						# Ya hay una abajo (existing_base) -> pones la nueva arriba.
						type_to_set = MIXED_SLAB_BASE + floor(existing_base) * MIXED_SLAB_SCALE + floor(base_id)
					else:
						# Apuntas a la mitad que ya está ocupada -> no cabe.
						return

		# ── romper solo LA MITAD que estás mirando (combinada o mezclada) ───
		# Una celda "combinada" (id + 0.75) o "mezclada" (id gigante) son en
		# realidad DOS losas, no un bloque nuevo. Al romper, en vez de vaciar
		# la celda entera, se revisa qué mitad estabas mirando y se deja la
		# otra puesta — igual que en Minecraft: picar una losa doble solo
		# quita la que apuntaste.
		if not is_placing and world and world.has_method("GetBlockGlobal"):
			var existing = world.GetBlockGlobal(block_v3)
			var hit_local_y = result.position.y - floor(result.position.y)

			if existing >= MIXED_SLAB_BASE:
				# 2 losas de materiales DISTINTOS -> deja la otra puesta,
				# con su orientación correspondiente (abajo o arriba).
				var rel = existing - MIXED_SLAB_BASE
				var bottom_mat = floor(rel / MIXED_SLAB_SCALE)
				var top_mat = round(rel - bottom_mat * MIXED_SLAB_SCALE)
				if hit_local_y < 0.5:
					_recoger_bloque_roto(bottom_mat)
					world.ModifyBlock(block_v3, top_mat + 0.5) # rompiste abajo -> queda arriba
				else:
					_recoger_bloque_roto(top_mat)
					world.ModifyBlock(block_v3, bottom_mat) # rompiste arriba -> queda abajo
				return

			var existing_base = floor(existing + 0.001)
			var existing_frac = existing - existing_base
			var is_combined_same = existing > 0.0 and BlockRegistry.IsBottomSlab(existing_base) and existing_frac > 0.65 and existing_frac < 0.85

			if is_combined_same:
				# 2 losas del MISMO material -> deja la otra puesta.
				if hit_local_y < 0.5:
					_recoger_bloque_roto(existing_base)
					world.ModifyBlock(block_v3, existing_base + 0.5) # rompiste abajo -> queda arriba
				else:
					_recoger_bloque_roto(existing_base)
					world.ModifyBlock(block_v3, existing_base) # rompiste arriba -> queda abajo
				return

		if not is_placing and world and world.has_method("GetBlockGlobal"):
			_recoger_bloque_roto(world.GetBlockGlobal(block_v3))
		if world:
			if world.has_method("ModifyBlock"):
				world.ModifyBlock(block_v3, type_to_set)
			elif world.has_method("modify_block"):
				world.modify_block(block_v3, type_to_set)
		if is_placing:
			_consumir_tras_colocar()


# ═════════════════════════════════════════════════════════════════════════════
# INVENTARIO / HOTBAR
# ═════════════════════════════════════════════════════════════════════════════
func _iniciar_inventario() -> void:
	_leer_modo()
	WorldManager.world_mode_changed.connect(func(_m, _x): _leer_modo())
	inventory.load_from_world(WorldManager.mundo_actual)   # inventory.json del mundo (si existe)
	inventory.item_dropped.connect(_on_item_soltado)
	if hotbar_ui:
		hotbar_ui.bind(inventory, stats)
		hotbar_ui.hud_visible_changed.connect(_on_hud_visible)
	if inventario_root:
		inventario_root.bind(inventory, stats)


func _leer_modo() -> void:
	_creativo = WorldManager.is_creative()


func es_creativo() -> bool:
	return _creativo


## true mientras el inventario está abierto (bloquea cámara, construir y movimiento).
func _ui_abierta() -> bool:
	return inventario_root != null and inventario_root.abierto


func _on_hud_visible(v: bool) -> void:
	var cruz := get_node_or_null("Camera3D/UI PRicipal/UI-Cursor")
	if cruz:
		cruz.visible = v


## Hook de ítems sueltos. TODO ItemEntity: instanciar una entidad de ítem frente al
## jugador. Por ahora el ítem soltado simplemente se descarta.
func _on_item_soltado(_pila: ItemStack) -> void:
	pass


func _guardar_inventario() -> void:
	if inventory:
		inventory.save_to_world()


func _exit_tree() -> void:
	_guardar_inventario()


## Survival/Hardcore: colocar consume 1. Creativo: no consume.
func _consumir_tras_colocar() -> void:
	if not _creativo:
		inventory.consume_selected(1)


## Survival/Hardcore: romper agrega el ítem al inventario (lleno → se pierde).
## TODO ItemEntity: en vez de perderlo, soltarlo al mundo.
func _recoger_bloque_roto(id_mundo: float) -> void:
	if _creativo:
		return
	var iid := ItemRegistry.item_id_for_world_block(id_mundo)
	if iid == "" or ItemRegistry.get_definition(iid).no_drop:
		return
	inventory.add_item(ItemStack.new(iid, 1))


## Clic central en Creativo: toma el bloque apuntado (pila completa).
func _elegir_bloque() -> void:
	if camera == null or not _creativo:
		return
	var world = get_parent()
	if world == null or not world.has_method("GetBlockGlobal"):
		return
	var center = get_viewport().get_visible_rect().size / 2
	var from = camera.project_ray_origin(center)
	var to = from + camera.project_ray_normal(center) * reach_distance
	var query = PhysicsRayQueryParameters3D.create(from, to)
	query.exclude = [get_rid()]
	var result = get_world_3d().direct_space_state.intersect_ray(query)
	if not result:
		return
	var probe = result.position - result.normal * 0.01
	var cell = Vector3i(floor(probe.x), floor(probe.y), floor(probe.z))
	var iid := ItemRegistry.item_id_for_world_block(world.GetBlockGlobal(cell))
	if iid != "" and not ItemRegistry.get_definition(iid).no_drop:
		inventory.pick_item(iid)
