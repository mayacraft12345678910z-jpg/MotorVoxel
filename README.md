# MotorVoxel
<img width="2400" height="274" alt="motor-voxel-logo" src="https://github.com/user-attachments/assets/1fde0eb6-9bef-499f-883b-8477674316cf" />

Motor voxel estilo Minecraft hecho en **Godot 4 (.NET)** con **C#** y **GDScript**.
Proyecto independiente, hecho como devlog en video.

> ## ⚠️ Términos de Uso y Licencia / Terms & Conditions

Este proyecto es de código abierto / libre acceso, pero está sujeto a las siguientes condiciones de redistribución y venta:

* **Sin Afiliación:** Este proyecto es totalmente independiente y no está afiliado, respaldado ni relacionado con ningún otro producto, marca o desarrollador externo.
* **Prohibición de reventa directa:** Está estrictamente prohibido vender, comercializar o cobrar por los archivos originales de este repositorio tal y como se descargan aquí. 
* **Condición para la venta de derivados:** Puedes modificar, adaptar o utilizar este proyecto para crear nuevos productos comerciales **únicamente si** aportas valor real, cambios significativos, código propio o recursos (`assets`) nuevos creados por ti que modifiquen sustancialmente la obra original.
* **Responsabilidad sobre los cambios:** Solo tienes derecho a comercializar aquello que tú hayas desarrollado o cambiado de forma legítima y que no formara parte del repositorio original.


## Descargar

Los archivos del proyecto no están en esta página: cada versión se publica en **Releases**.

➡️ **[Ir a la última versión](../../releases/latest)** (también está en la columna derecha de esta página, en "Releases").

## Características

- Bloques definidos en archivos **JSON** (un archivo por bloque): el juego los lee al iniciar.
- Generación de terreno procedural: llanuras, montañas, cuevas y océanos, con **sistema de biomas**.
- Guardado y carga de mundos con chunks comprimidos (RLE + GZip) y manejo de semillas.
- Distancia de renderizado configurable de 2 hasta 128 chunks.
- Optimizado para gráficos integrados: face culling, oclusión, subida de mallas diferida y reutilización de chunks.
- Menús, opciones, selector de skins y menú de pausa al estilo de Minecraft Java.
- Sistema de paquetes de recursos (`.zip`) cargados en tiempo de ejecución.
- Ciclo de día y noche con nubes configurables.

## Requisitos

- **Godot 4.6 versión .NET** (no sirve la versión estándar sin .NET).
- El **SDK de .NET** que pida tu versión de Godot .NET.

## Cómo ejecutarlo

1. Descarga el `.zip` de la versión que quieras desde **Releases** y descomprímelo.
2. Abre Godot .NET, pulsa **Importar** y elige el archivo `project.godot`.
3. Si faltan texturas o ves todo **morado y negro**, usa el programa **MotorVoxelTextureLoader** y sigue los pasos que te indica la app.
4. Pulsa **Compilar** (el martillo) y luego **Ejecutar** (F5).

## Texturas

Este proyecto **no incluye texturas, fuentes ni sonidos de Minecraft**, porque son propiedad de Mojang/Microsoft. Puedes crear las tuyas propias o usar los mio  Para instalarlas usa **MotorVoxelTextureLoader**.

## Documentación

- **Manual técnico** (`README_MotorVoxel.pdf`): explica cada script, sus funciones y cómo extenderlo. Si necesitas saber algo específico de un script, búscalo ahí.
- **Leeme.txt**: guía rápida con los pasos básicos.

Ambos archivos se adjuntan en cada versión.
