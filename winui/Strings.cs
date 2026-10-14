using System.Globalization;

namespace WinSpotlight;

/// <summary>UI text in English or Spanish. Follows the Windows display language unless
/// config.json sets "language" to "en" or "es". The backend makes the same choice.</summary>
public static class L
{
    public static readonly bool Es = Pick();

    private static bool Pick()
    {
        try
        {
            var lang = SetupWindow.LoadConfig()["language"]?.GetValue<string>();
            if (lang is "en" or "es") return lang == "es";
        }
        catch { }
        return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "es";
    }

    private static string T(string en, string es) => Es ? es : en;

    public static string Date(DateTime d) => Es
        ? d.ToString("d 'de' MMM 'de' yyyy, H:mm", new CultureInfo("es"))
        : d.ToString("MMM d, yyyy 'at' h:mm tt", CultureInfo.InvariantCulture);

    // ---- search window ---------------------------------------------------------------
    public static string SearchPlaceholder => T("Spotlight Search", "Búsqueda en Spotlight");
    public static string AppearanceTip => T("Appearance (Ctrl+,)", "Apariencia (Ctrl+,)");
    public static string Hints => T("↵ Open    Ctrl+↵ Show in Explorer", "↵ Abrir    Ctrl+↵ Mostrar en el Explorador");
    public static string HintFix(string llm) => T($"    Tab Fix with {llm}", $"    Tab Arreglar con {llm}");
    public static string HintsSettings => T("Esc Close    Changes are saved automatically", "Esc Cerrar    Los cambios se guardan solos");
    public static string StartingBackend => T("Starting search backend…", "Iniciando el motor de búsqueda…");
    public static string BackendUnreachable => T("Search backend not reachable", "No se puede contactar el motor de búsqueda");
    public static string ApplyingSettings => T("Applying settings…", "Aplicando ajustes…");
    public static string HotkeyTaken(string hk) => T($"{hk} is taken; edit config.json", $"{hk} está ocupado; cámbialo en config.json");

    public static string MenuSearch => T("Search", "Buscar");
    public static string MenuAppearance => T("Appearance…", "Apariencia…");
    public static string MenuReindex => T("Rebuild index now", "Reconstruir el índice");
    public static string MenuSetup => T("Setup…", "Configuración inicial…");
    public static string MenuConfig => T("Open config", "Abrir config.json");
    public static string MenuQuit => T("Quit WinSpotlight", "Salir de WinSpotlight");

    public static string Calculator => T("Calculator", "Calculadora");
    public static string TopHit => T("Top Hit", "Mejor resultado");
    public static string Fix(string file) => T($"Fix {file}", $"Arreglar {file}");
    public static string CalcHint => T("Calculator · ↵ copies the result", "Calculadora · ↵ copia el resultado");
    public static string WebPage => T("Web Page", "Página web");
    public static string LastVisited => T("Last visited", "Última visita");
    public static string Modified => T("Modified", "Modificado");
    public static string Where => T("Where", "Ubicación");
    public static string Application => T("Application", "Aplicación");

    public static string Kind(string kind) => kind switch
    {
        "app" => Application,
        "folder" => T("Folder", "Carpeta"),
        "document" => T("Document", "Documento"),
        "code" => T("Code", "Código"),
        "image" => T("Image", "Imagen"),
        "video" => T("Movie", "Película"),
        "audio" => T("Music", "Música"),
        "archive" => T("Archive", "Archivo comprimido"),
        "other" => T("File", "Archivo"),
        var k => char.ToUpperInvariant(k[0]) + k[1..],
    };

    public static string AgentIntro(string llm, string target) => T(
        $"Press Tab or ↵ and {llm}'s agents will edit\n{target}\nA .winspotlight.bak backup is kept.",
        $"Pulsa Tab o ↵ y los agentes de {llm} editarán\n{target}\nSe guarda una copia .winspotlight.bak.");
    public static string Thinking => T("Thinking…", "Pensando…");
    public static string Starting => T("Starting…", "Empezando…");
    public static string WorkingOn(string target) => T($"Working on {target}", $"Trabajando en {target}");
    public static string CantReach(string llm) => T($"Couldn't reach {llm}", $"No se pudo contactar a {llm}");

    // ---- appearance panel ----------------------------------------------------------------
    public static string Appearance => T("Appearance", "Apariencia");
    public static string Mode => T("Mode", "Modo");
    public static string Auto => T("Auto", "Auto");
    public static string Light => T("Light", "Claro");
    public static string Dark => T("Dark", "Oscuro");
    public static string AccentColor => T("Accent color", "Color de acento");
    public static string Glass => T("Glass", "Cristal");
    public static string Blur => T("Blur", "Desenfoque");
    public static string Transparency => T("Transparency", "Transparencia");
    public static string Vibrancy => T("Vibrancy", "Vibrancia");
    public static string EdgeHighlight => T("Edge highlight", "Brillo en el borde");
    public static string Shadow => T("Shadow", "Sombra");
    public static string Results => T("Results", "Resultados");
    public static string ShowPreview => T("Show preview pane", "Mostrar vista previa");
    public static string RestoreDefaults => T("Restore Defaults", "Restaurar valores");

    public static string AccentName(string key) => key switch
    {
        "blue" => T("Blue", "Azul"),
        "purple" => T("Purple", "Morado"),
        "pink" => T("Pink", "Rosa"),
        "red" => T("Red", "Rojo"),
        "orange" => T("Orange", "Naranja"),
        "yellow" => T("Yellow", "Amarillo"),
        "green" => T("Green", "Verde"),
        _ => T("Graphite", "Grafito"),
    };

    // ---- setup wizard ------------------------------------------------------------------
    public static string SetupTitle => T("WinSpotlight Setup", "Configuración de WinSpotlight");
    public static string Welcome => T("Welcome to WinSpotlight", "Te damos la bienvenida a WinSpotlight");
    public static string WelcomeBody => T(
        "WinSpotlight searches apps, files, and the contents of your documents by meaning, right from a hotkey. Let's choose what it should look through. You can rerun this setup from the tray icon at any time.",
        "WinSpotlight busca aplicaciones, archivos y el contenido de tus documentos por su significado, con solo pulsar un atajo. Elijamos dónde debe buscar. Puedes repetir esta configuración desde el icono de la bandeja cuando quieras.");
    public static string OpenWith(string hk) => T($"Open WinSpotlight any time with {hk}.", $"Abre WinSpotlight en cualquier momento con {hk}.");
    public static string Step(int i, int n) => T($"WinSpotlight setup · Step {i} of {n}", $"Configuración de WinSpotlight · Paso {i} de {n}");
    public static string FoldersTitle => T("Searched folders", "Carpetas en las que buscar");
    public static string FoldersBody => T(
        "The contents of documents in these folders are indexed for semantic search.",
        "El contenido de los documentos de estas carpetas se indexa para buscar por significado.");
    public static string AddFolder => T("Add folder", "Añadir carpeta");
    public static string Remove => T("Remove", "Quitar");
    public static string AllDrives => T("Also find files by name anywhere on all drives", "Encontrar también archivos por nombre en todas las unidades");
    public static string HistoryTitle => T("Browser history", "Historial del navegador");
    public static string HistoryBody => T(
        "Show matching pages from Chrome, Edge, Brave, Vivaldi, Opera, and Firefox below your local results. History is read on this PC only and never stored by WinSpotlight.",
        "Muestra páginas coincidentes de Chrome, Edge, Brave, Vivaldi, Opera y Firefox debajo de los resultados locales. El historial se lee solo en este PC y WinSpotlight nunca lo guarda.");
    public static string HistoryOn => T("Include browser history", "Incluir el historial del navegador");
    public static string HistoryOff => T("Don't include browser history", "No incluir el historial del navegador");
    public static string LlaBody => T(
        "LLA is a local mixture-of-experts model that fixes files for you: type “fix app.py it crashes on start” and press Tab. Everything runs on this PC.",
        "LLA es un modelo local de mezcla de expertos que arregla archivos por ti: escribe “fix app.py it crashes on start” y pulsa Tab. Todo se ejecuta en este PC.");
    public static string LlaOn => T("Use LLA", "Usar LLA");
    public static string LlaOff => T("Don't use LLA", "No usar LLA");
    public static string LlaDownload => T("Download LLA (4.1 GB)", "Descargar LLA (4,1 GB)");
    public static string LlaInstalled => T("✓ LLA is installed.", "✓ LLA está instalado.");
    public static string DownloadingLlama => T("Downloading llama.cpp…", "Descargando llama.cpp…");
    public static string ResumeDownload => T("Resume download", "Reanudar la descarga");
    public static string DownloadFailed => T("Download failed: ", "La descarga falló: ");
    public static string Ram(double gb) => T($"This PC has {gb:0} GB of RAM.", $"Este PC tiene {gb:0} GB de RAM.");
    public static string AllSet => T("You're all set", "Todo listo");
    public static string Back => T("Back", "Atrás");
    public static string Next => T("Next", "Siguiente");
    public static string Save => T("Save", "Guardar");
    public static string Finish => T("Finish", "Terminar");
    public static string SumFolders(int n) => Es
        ? $"• Indexando el contenido de {n} carpeta{(n == 1 ? "" : "s")}"
        : $"• Indexing contents of {n} folder{(n == 1 ? "" : "s")}";
    public static string SumAllDrives => T("• Finding files by name on all drives", "• Buscando archivos por nombre en todas las unidades");
    public static string SumFoldersOnly => T("• Finding files only inside those folders", "• Buscando archivos solo dentro de esas carpetas");
    public static string SumHistoryOn => T("• Including browser history", "• Incluyendo el historial del navegador");
    public static string SumHistoryOff => T("• Browser history off", "• Historial del navegador desactivado");
    public static string SumLlaOn => T("• LLA on", "• LLA activado");
    public static string SumLlaOff => T("• LLA off", "• LLA desactivado");
    public static string SumRestart => T("\nWinSpotlight will now restart its index in the background.", "\nWinSpotlight reiniciará ahora su índice en segundo plano.");
}
