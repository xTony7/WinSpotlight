[English](README.md) · **Español**

# WinSpotlight — Spotlight para Windows

> [!WARNING]
> **Problema de seguridad conocido — sin mantenimiento.** En esta versión el motor de búsqueda local
> acepta peticiones de cualquier programa del PC, incluidas las páginas web abiertas en el navegador,
> que podrían pedirle abrir archivos o programas. Este repositorio queda archivado en la versión 1.1.0
> y no recibirá correcciones. Úsalo bajo tu propio riesgo, idealmente solo para estudiarlo.

Un lanzador al estilo de Spotlight para Windows, con el aspecto de macOS. Pulsa **Alt+Espacio**
para buscar aplicaciones, archivos, el contenido de tus documentos y el historial del navegador por
su significado, no solo por el nombre. También puede arreglar archivos por ti con **LLA**, un
modelo local de mezcla de expertos: escribe `fix app.py it crashes on start` y pulsa Tab.

Todo se ejecuta en tu PC.

WinSpotlight es un fork de [LSearch](https://github.com/blueprintnerd/LSearch), de blueprintnerd.
Este fork añade:

- Una ventana de cristal esmerilado inspirada en Spotlight de macOS (barra de búsqueda en forma de
  cápsula, borde Liquid Glass, sombra suave y selección con el color de acento).
- Un panel de **Apariencia**: tema, color de acento, desenfoque, transparencia, vibrancia y más.
- Interfaz en español e inglés según el idioma de Windows.
- **Alt+Espacio** como atajo por defecto (antes era Win+Y).

## Instalación

Descarga `WinSpotlight-Setup.exe` desde [Releases](../../releases) cuando haya una publicada, o
[compílalo desde el código](#compilar-desde-el-código). El instalador funciona por usuario, sin
pedir permisos de administrador, y no necesita ni Python ni .NET. El asistente inicial pregunta qué
carpetas indexar, si incluir el historial del navegador y, en equipos con 16 GB de RAM o más, si
descargar LLA (4,1 GB, desde [Hugging Face](https://huggingface.co/scutoid/2x4B)).

| Tecla | Acción |
| --- | --- |
| Alt+Espacio | Mostrar u ocultar WinSpotlight |
| ↑ / ↓ | Mover la selección |
| ↵ | Abrir (en la calculadora, copia el resultado) |
| Ctrl+↵ | Mostrar en el Explorador |
| Tab | Ejecutar una tarea de LLA, p. ej. `fix "C:\code\app.py" it crashes on start` |
| Ctrl+, | Ajustes de apariencia |
| Esc | Borrar y, después, ocultar |

**¿Vienes de LSearch?** Al iniciarse por primera vez, WinSpotlight mueve tu configuración, índice y
modelos desde `%APPDATA%\LSearch` y `%LOCALAPPDATA%\LSearch`. Después desinstala LSearch, porque
las dos aplicaciones usan el mismo atajo.

## Requisitos del sistema

| | Solo búsqueda | Con LLA |
| --- | --- | --- |
| Sistema | Windows 10 1809+ (64 bits) | igual |
| RAM | 8 GB | 16 GB mínimo, 32 GB recomendado |
| CPU | 4 hilos | 4 núcleos con AVX2; 8 o más recomendado |
| Disco | ~400 MB más el índice | +4,1 GB para el modelo |

LLA escribe su razonamiento antes de cada paso, así que es lento en CPUs modestas (unos 2 tokens/s
con 4 hilos).

## Apariencia

Haz clic en el engranaje de la barra de búsqueda, pulsa Ctrl+, o usa la bandeja → *Apariencia…*:

| Ajuste | Qué hace |
| --- | --- |
| Modo | Auto (sigue a Windows), Claro u Oscuro |
| Color de acento | Los ocho acentos de macOS; se usan en la selección, los interruptores y los sliders |
| Desenfoque | Cuánto se desenfoca el escritorio detrás del cristal (0 = cristal transparente) |
| Transparencia | Cuánto del escritorio se ve a través del tinte |
| Vibrancia | Intensidad del color de lo que se ve detrás, como en macOS |
| Brillo en el borde | El borde brillante tipo Liquid Glass |
| Sombra | Sombra suave alrededor de la ventana |
| Mostrar vista previa | Vista previa del archivo a la derecha de los resultados |

Los cambios se aplican al instante y se guardan en la sección `appearance` de config.json. El
cristal desenfoca una captura del escritorio tomada cada vez que se abre la ventana, así que un
video que se reproduzca detrás se verá congelado hasta la siguiente vez que la abras.

## Idioma

La interfaz, el asistente inicial y el instalador están en español o inglés según el idioma de
Windows. Para forzar uno, añade `"language": "es"` o `"language": "en"` a config.json y reinicia
WinSpotlight.

## Cómo funciona

- **Índice de nombres:** recorre todas las unidades fijas y las guarda en SQLite FTS5 (omite las
  carpetas del sistema).
- **Índice de contenido:** los archivos de texto, código, PDF y DOCX de las carpetas elegidas se
  dividen en fragmentos, se convierten en embeddings en local (`BAAI/bge-small-en-v1.5` con
  fastembed/ONNX) y también se indexan para búsqueda por palabras clave.
- **Clasificación:** fusión por rango recíproco de coincidencias por nombre, significado y palabras
  clave. Las consultas en lenguaje natural ("ese documento con las condiciones del alquiler") se
  analizan y un cross-encoder reordena los mejores candidatos. Los resultados rápidos aparecen al
  instante y los reordenados unos 2 s después.
- **Historial del navegador:** Chrome, Edge, Brave, Vivaldi, Opera y Firefox, debajo de los
  resultados locales.
- **Tareas de LLA:** un agente Fixer edita el archivo con herramientas (leer, reemplazar,
  escribir) y luego un agente Reviewer revisa el cambio. Las ediciones se limitan a la carpeta del
  archivo, se guarda una copia `.winspotlight.bak` y los comandos de consola están desactivados
  salvo que `agent.allow_commands` sea true.

La configuración está en `%APPDATA%\WinSpotlight\config.json` (bandeja → *Abrir config.json*).

### Usar tu propio modelo

LLA funciona con `llama-server` de llama.cpp en el puerto 1114. Para usar otro modelo, apunta `llm`
a cualquier servidor compatible con OpenAI (llama.cpp, Ollama, LM Studio, vLLM):

```json
"llm": { "enabled": true, "display_name": "MiModelo",
         "base_url": "http://127.0.0.1:11434/v1", "model": "mi-modelo", "model_path": "" }
```

O pon en `model_path` un archivo `.gguf` y WinSpotlight iniciará `llama-server` con él cuando haga
falta.

## Compilar desde el código

Necesitas Python 3.10+, el SDK de .NET 10 y, para el instalador,
[Inno Setup 6](https://jrsoftware.org/isinfo.php).

```
pip install -r requirements.txt pyinstaller
powershell -File packaging\build.ps1     # -> dist\WinSpotlight\ y dist\WinSpotlight-Setup.exe
```

Para desarrollar, `cd winui && dotnet build -c Release` y ejecuta `WinSpotlight.exe` desde `bin\`.
Arranca el backend de Python (`pythonw -m winspotlight.server`, localhost:52123) desde el código.
Opciones de depuración: `--show`, `--pin`, `--query <texto>`, `--setup`.

## Licencia

Apache License 2.0; consulta [LICENSE](LICENSE). Basado en LSearch, de blueprintnerd; los cambios
de este fork están en el historial de commits.
