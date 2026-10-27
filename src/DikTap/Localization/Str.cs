namespace DikTap.Localization;

// User-visible texts. Each one carries its Spanish and English versions side by side: there are no keys that can
// end up untranslated. Technical logs (app.log) are not translated.
internal static class Str
{
    private static string P(string es, string en) => Loc.Pick(es, en);

    // ---- Tray and application ----
    public static string TrayShortcut(string description, bool active) => P($"Atajo: {(active ? description : "inactivo")}", $"Shortcut: {(active ? description : "inactive")}");
    public static string TraySettings => P("Configuración…", "Settings…");
    public static string TrayCopyLast => P("Copiar último dictado (Ctrl+Alt+C)", "Copy last dictation (Ctrl+Alt+C)");
    public static string TrayStartup => P("Iniciar con Windows", "Start with Windows");
    public static string TrayExit => P("Salir", "Exit");
    public static string TipReady(string description) => P($"listo ({description})", $"ready ({description})");
    public static string TipRecording => P("grabando", "recording");
    public static string TipTranscribing => P("transcribiendo", "transcribing");
    public static string TipWorking => P("trabajando", "working");
    public static string LastCopied => P("Último dictado copiado al portapapeles", "Last dictation copied to the clipboard");
    public static string NoLastDictation => P("No hay ningún dictado previo para copiar", "No previous dictation to copy");
    public static string ClipboardBusyRetry => P("El portapapeles está ocupado; inténtalo de nuevo.", "The clipboard is busy; try again.");
    public static string StartupToggleFailed => P("No se pudo cambiar el inicio automático.", "Could not change automatic startup.");
    public static string UninstallPathUnsafe => P(
        "La ruta del ejecutable o de los datos contiene caracteres que impiden la limpieza automática. Elimina a mano lo indicado en el README.",
        "The path of the executable or data folder contains characters that prevent automatic cleanup. Remove the items listed in the README by hand.");
    public static string UninstallKeyFailed => P(
        "No se pudo eliminar la clave o el inicio automático; no se ha desinstalado nada más.",
        "Could not remove the key or automatic startup; nothing else was uninstalled.");
    public static string UninstallFailed => P(
        "No se pudo desinstalar. Revisa el registro o elimina los datos a mano (consulta el README).",
        "Uninstall failed. Check the log or remove the data by hand (see the README).");

    // ---- Indicator ----
    public static string OverlayPreparing => P("Preparando…", "Preparing…");
    public static string OverlayRecording => P("Grabando", "Recording");
    public static string OverlayTranscribing => P("Transcribiendo…", "Transcribing…");
    public static string OverlayTranscribingNote(string note) => P($"{note} · Transcribiendo…", $"{note} · Transcribing…");
    public static string OverlayCancelled => P("Cancelado", "Cancelled");
    public static string NoteMicDisconnected => P("Micrófono desconectado", "Microphone disconnected");
    public static string NoteLimitReached => P("Límite de grabación alcanzado", "Recording limit reached");
    public static string PastedAfterMicLost => P(
        "Micrófono desconectado: se pegó lo grabado hasta ese momento",
        "Microphone disconnected: what was recorded so far was pasted");

    // ---- Dictation (controller) ----
    public static string MissingKey => P("Falta la API key. Introdúcela en Configuración (icono de la bandeja).", "The API key is missing. Enter it in Settings (tray icon).");
    public static string MicLostTooShort => P("El micrófono se desconectó antes de captar audio suficiente.", "The microphone was disconnected before enough audio was captured.");
    public static string TooShort => P("Grabación demasiado corta.", "Recording too short.");
    public static string NoSound => P("No se captó sonido: el micrófono está silenciado, bloqueado o sin señal.", "No sound was captured: the microphone is muted, blocked or has no signal.");
    public static string NoSpeech => P("No se detectó voz en la grabación.", "No speech was detected in the recording.");
    public static string Unexpected => P("Error inesperado. Inténtalo de nuevo.", "Unexpected error. Try again.");

    // ---- Audio ----
    public static string MicAccessDenied => P(
        "Windows no permite usar el micrófono. Revisa Configuración > Privacidad y seguridad > Micrófono.",
        "Windows does not allow microphone access. Check Settings > Privacy & security > Microphone.");
    public static string MicNotFound => P("No se encontró el micrófono. Conéctalo o elígelo en Configuración.", "Microphone not found. Connect it or choose it in Settings.");
    public static string MicDisconnected => P("El micrófono se desconectó o cambió. Vuelve a empezar.", "The microphone was disconnected or changed. Start again.");
    public static string MicInUse => P("El micrófono está en uso exclusivo por otra aplicación.", "The microphone is in exclusive use by another application.");
    public static string AudioServiceDown => P("El servicio de audio de Windows no está en ejecución.", "The Windows audio service is not running.");
    public static string AudioError(int hr, string step) => P($"Error de audio de Windows (0x{hr:X8}) en {step}.", $"Windows audio error (0x{hr:X8}) in {step}.");
    public static string CaptureUnexpected => P("Error inesperado al capturar audio.", "Unexpected error while capturing audio.");
    public static string MicSilent => P("El micrófono no está entregando audio.", "The microphone is not delivering audio.");
    public static string NoMicAvailable => P("No hay ningún micrófono disponible.", "No microphone is available.");
    public static string SelectedMicUnavailable => P(
        "El micrófono seleccionado no está disponible. Elige otro en Configuración o usa el predeterminado.",
        "The selected microphone is not available. Choose another in Settings or use the default.");
    public static string UnsupportedMicFormat(int tag) => P($"Formato de audio del micrófono no soportado (código {tag}).", $"Unsupported microphone audio format (code {tag}).");
    public static string InvalidAudioFormat => P("El micrófono devolvió un formato de audio inválido.", "The microphone returned an invalid audio format.");
    public static string UnsupportedFormat(object format) => P($"Formato de audio no soportado ({format}).", $"Unsupported audio format ({format}).");
    public static string UnsupportedFormatAlignment(object format, int align) => P($"Formato de audio no soportado ({format}, alineación {align}).", $"Unsupported audio format ({format}, alignment {align}).");

    // ---- Key ----
    public static string KeyTooLong => P("La API key es demasiado larga.", "The API key is too long.");
    public static string KeyBadLength => P("La API key no tiene una longitud válida.", "The API key does not have a valid length.");
    public static string KeyBadChars => P("La API key contiene espacios o caracteres no válidos.", "The API key contains spaces or invalid characters.");

    // ---- Hotkeys ----
    public static string HotkeyDescription(bool winH, bool backup) => winH
        ? (backup ? P("Win+H (respaldo: Ctrl+Alt+Espacio)", "Win+H (backup: Ctrl+Alt+Space)") : "Win+H")
        : P("Ctrl+Alt+Espacio", "Ctrl+Alt+Space");
    public static string WinHFailed => P("No se pudo activar Win+H; se usa Ctrl+Alt+Espacio.", "Could not enable Win+H; Ctrl+Alt+Space is used instead.");
    public static string CtrlAltSpaceInUse => P("Ctrl+Alt+Espacio ya está en uso por otra aplicación.", "Ctrl+Alt+Space is already in use by another application.");
    public static string CtrlAltSpaceFailed(int err) => P($"No se pudo registrar Ctrl+Alt+Espacio (error {err}).", $"Could not register Ctrl+Alt+Space (error {err}).");
    public static string WinHStopped => P("Win+H dejó de funcionar; usa Ctrl+Alt+Espacio.", "Win+H stopped working; use Ctrl+Alt+Space.");
    public static string WinHStoppedRestart => P("Win+H dejó de funcionar. Reinicia la aplicación.", "Win+H stopped working. Restart the application.");

    // ---- Paste ----
    public static string ClipboardBusyThrow => P(
        "El portapapeles está ocupado por otra aplicación. Usa «Copiar último dictado» en el icono de la bandeja.",
        "The clipboard is in use by another application. Use “Copy last dictation” in the tray icon.");
    public static string ModifiersHeld => P("Sigues pulsando teclas modificadoras", "You are still holding modifier keys");
    public static string PasteBlocked => P("Windows bloqueó el pegado automático", "Windows blocked the automatic paste");
    public static string NoDestination => P("No se pudo identificar la ventana de destino", "The target window could not be identified");
    public static string DestinationClosed => P("La ventana de destino se cerró", "The target window was closed");
    public static string DestinationNotActive => P("La ventana original ya no estaba activa al terminar", "The original window was no longer active at the end");
    public static string FocusChanged => P("Cambió el campo con foco", "The focused field changed");
    public static string DestinationElevated => P("La aplicación de destino se ejecuta como administrador", "The target application runs as administrator");
    public static string DestinationUnknown => P("No se pudo validar el destino", "The target could not be validated");
    public static string PastedNoPreviousClipboard => P("Pegado. No se pudo conservar el portapapeles anterior.", "Pasted. The previous clipboard could not be preserved.");
    public static string ManualCopied(string reason) => P($"{reason}. Texto copiado; pega donde quieras.", $"{reason}. Text copied; paste wherever you like.");
    public static string ManualBusy(string reason) => P(
        $"{reason}. Además el portapapeles está ocupado: usa «Copiar último dictado» en la bandeja.",
        $"{reason}. The clipboard is also busy: use “Copy last dictation” in the tray.");

    // ---- Startup ----
    public static string ExePathUnknown => P("No se pudo determinar la ruta del ejecutable.", "Could not determine the executable path.");
    public static string ExePathTooLong => P(
        "La ruta del ejecutable es demasiado larga para el inicio automático. Muévelo a una carpeta más corta.",
        "The executable path is too long for automatic startup. Move it to a shorter folder.");

    // ---- OpenAI ----
    public static string ApiTimeout(double seconds) => P($"OpenAI no respondió en {seconds:0} s. Inténtalo de nuevo.", $"OpenAI did not respond within {seconds:0} s. Try again.");
    public static string ApiConnectFailed => P("No se pudo conectar con OpenAI. Revisa la conexión, el proxy o el cortafuegos.", "Could not connect to OpenAI. Check your connection, proxy or firewall.");
    public static string ApiInterrupted => P("Se interrumpió la conexión con OpenAI. Inténtalo de nuevo.", "The connection to OpenAI was interrupted. Try again.");
    public static string ApiUnexpectedResponse => P("Respuesta inesperada de OpenAI.", "Unexpected response from OpenAI.");
    public static string Api400 => P("OpenAI rechazó la petición (400). Revisa el vocabulario/contexto en Configuración o el audio.", "OpenAI rejected the request (400). Check the vocabulary/context in Settings or the audio.");
    public static string Api401 => P("API key inválida o revocada (401). Actualízala en Configuración.", "Invalid or revoked API key (401). Update it in Settings.");
    public static string Api403 => P("Acceso denegado (403). Tu proyecto o región no tiene acceso a gpt-transcribe.", "Access denied (403). Your project or region has no access to gpt-transcribe.");
    public static string Api404 => P("Modelo gpt-transcribe no disponible para esta cuenta (404).", "The gpt-transcribe model is not available for this account (404).");
    public static string Api413 => P("El audio supera el tamaño permitido por OpenAI (413).", "The audio exceeds the size allowed by OpenAI (413).");
    public static string Api429Quota => P("Sin crédito o cuota en tu cuenta de OpenAI (429).", "No credit or quota left in your OpenAI account (429).");
    public static string Api429 => P("Límite de peticiones de OpenAI alcanzado (429). Espera un momento.", "OpenAI rate limit reached (429). Wait a moment.");
    public static string Api5xx(int status) => P($"Error del servidor de OpenAI ({status}). Inténtalo de nuevo.", $"OpenAI server error ({status}). Try again.");
    public static string ApiOther(int status) => P($"OpenAI devolvió un error inesperado ({status}).", $"OpenAI returned an unexpected error ({status}).");

    // ---- Settings ----
    public static string SettingsTitle => P($"{AppInfo.Name} · Configuración", $"{AppInfo.Name} · Settings");
    public static string WelcomeSubtitle => P(
        "Dictado por voz para cualquier aplicación de Windows.",
        "Voice dictation for any Windows application.");
    public static string ShortcutDictate => P("Empezar / parar", "Start / stop");
    public static string ShortcutCopyLast => P("Copiar último", "Copy last");
    public static string AboutVersion(string version) => P($"Versión {version}", $"Version {version}");
    public static string LinkRepository => P("Repositorio", "Repository");
    public static string LinkReportBug => P("Informar de un error", "Report a bug");
    public static string LinkRequestFeature => P("Sugerir una mejora", "Request a feature");
    public static string LinkLicense => P("Licencia GPL-3.0", "GPL-3.0 license");
    public static string AboutLicenseNote => P("Software libre (GPL-3.0), sin garantía", "Free software (GPL-3.0), no warranty");
    public static string BtnCheckUpdates => P("Buscar actualizaciones", "Check for updates");
    public static string CheckingUpdates => P("Buscando actualizaciones…", "Checking for updates…");
    public static string AlreadyLatestVersion => P("Tienes la versión más reciente.", "You have the latest version.");
    public static string UpdateAvailable(string version) => P($"Nueva versión disponible: v{version}", $"New version available: v{version}");
    public static string BtnInstallUpdate => P("Actualizar y reiniciar", "Update and restart");
    public static string DownloadingUpdate => P("Descargando…", "Downloading…");
    public static string DownloadingUpdatePercent(int percent) => P($"Descargando… {percent}%", $"Downloading… {percent}%");
    public static string VerifyingUpdate => P("Verificando firma SHA-256…", "Verifying SHA-256…");
    public static string InstallingUpdate => P("Instalando y reiniciando…", "Installing and restarting…");
    public static string UpdateHashMismatch => P("La comprobación de integridad (SHA-256) falló. Actualización cancelada.", "Integrity check (SHA-256) failed. Update cancelled.");
    public static string UpdateInvalidPackage => P("El paquete descargado no contiene un ejecutable válido.", "The downloaded package does not contain a valid executable.");
    public static string UpdateLaunchFailed => P("No se pudo iniciar el proceso de actualización.", "Could not launch the update process.");
    public static string UpdateRateLimited => P("Límite de peticiones de GitHub excedido; inténtalo más tarde.", "GitHub API rate limit exceeded; please try again later.");
    public static string UpdateCheckFailed => P("No se pudo comprobar si hay actualizaciones.", "Could not check for updates.");
    public static string AppUpdated(string version) => P($"DikTap se ha actualizado a la versión {version}", $"DikTap was updated to version {version}");
    public static string FirstUse => P(
        "Primer uso: introduce tu API key de OpenAI para empezar. Tras guardar, la aplicación queda en la bandeja del sistema.",
        "First use: enter your OpenAI API key to get started. After saving, the application stays in the system tray.");
    public static string HdrKey => P("API key de OpenAI", "OpenAI API key");
    public static string BtnSaveKey => P("Guardar clave", "Save key");
    public static string BtnChangeKey => P("Cambiar clave", "Change key");
    public static string BtnDelete => P("Eliminar", "Delete");
    public static string NoteKey => P(
        "La clave se guarda en el Administrador de credenciales de Windows para tu usuario. Se usa tu propia facturación de API de OpenAI.",
        "The key is stored in Windows Credential Manager for your user. Your own OpenAI API billing is used.");
    public static string KeyStored => P("Clave guardada para este usuario de Windows.", "Key stored for this Windows user.");
    public static string KeyNone => P("No hay ninguna clave guardada.", "No key is stored.");
    public static string KeySaved => P("Clave guardada.", "Key saved.");
    public static string KeySaveFailed => P("No se pudo guardar la clave en el Administrador de credenciales.", "Could not store the key in Credential Manager.");
    public static string KeyDeleted => P("Clave eliminada.", "Key deleted.");
    public static string KeyDeleteFailed => P("No se pudo eliminar la clave.", "Could not delete the key.");
    public static string HdrMic => P("Micrófono", "Microphone");
    public static string MicDefault => P("Predeterminado de Windows", "Windows default");
    public static string MicSavedMissing => P("(micrófono guardado no disponible)", "(saved microphone not available)");
    public static string HdrShortcut => P("Atajo", "Shortcut");
    public static string ModeWinH => P("Win+H (reemplaza el dictado de Windows mientras la aplicación esté abierta)", "Win+H (replaces Windows dictation while the application is open)");
    public static string ModeCtrlAltSpace => P("Ctrl+Alt+Espacio (alternativa)", "Ctrl+Alt+Space (alternative)");
    public static string NoteEscape => P("Escape cancela el dictado en curso.", "Escape cancels the dictation in progress.");
    public static string HotkeyStatusActive(string description, string? warning) => P($"Atajo activo: {description}. {warning}", $"Shortcut active: {description}. {warning}").Trim();
    public static string HotkeyStatusInactive(string? warning) => P($"Atajo inactivo. {warning}", $"Shortcut inactive. {warning}").Trim();
    public static string HdrContext => P("Contexto de dictado (opcional)", "Dictation context (optional)");
    public static string LblLanguages => P("Idiomas esperados (códigos separados por comas)", "Expected languages (comma-separated codes)");
    public static string LblKeywords => P("Vocabulario (términos separados por comas o líneas)", "Vocabulary (terms separated by commas or lines)");
    public static string LblContext => P("Contexto breve", "Short context");
    public static string BtnResetContext => P("Restablecer", "Reset");
    public static string HdrClipboard => P("Portapapeles tras pegar", "Clipboard after pasting");
    public static string ClipRestore => P("Restaurar lo que tenía copiado (el dictado no queda en el portapapeles)", "Restore what I had copied (the dictation does not stay on the clipboard)");
    public static string ClipKeep => P("Dejar el dictado en el portapapeles (Ctrl+V lo repite; no entra en el historial)", "Keep the dictation on the clipboard (Ctrl+V repeats it; not added to history)");
    public static string ClipKeepHistory => P("Dejar el dictado en el portapapeles y añadirlo al historial (Win+V)", "Keep the dictation on the clipboard and add it to history (Win+V)");
    public static string NoteClipboard => P(
        "El historial de Win+V solo guarda elementos si lo tienes activado en Windows (Configuración > Sistema > Portapapeles). El dictado nunca se sincroniza con la nube.",
        "The Win+V history only stores items if it is enabled in Windows (Settings > System > Clipboard). The dictation is never synced to the cloud.");
    public static string HdrOptions => P("Opciones", "Options");
    public static string HdrLanguage => "Idioma de la aplicación / App language";
    public static string NoteLanguage => P(
        "Idioma de la interfaz de la aplicación. No afecta a los idiomas en los que dictas.",
        "User interface language for the application. Does not affect the dictation language.");
    public static string LanguageAuto => P("Automático (idioma de Windows)", "Automatic (Windows language)");
    public static string LanguageSpanish => "Español";
    public static string LanguageEnglish => "English";
    public static string ChkStartup => TrayStartup;
    public static string ChkSingleClickCopy => P(
        "Clic izquierdo en la bandeja copia el último dictado",
        "Left-clicking the tray icon copies the last dictation");
    public static string StartupDisabledByWindows => P(
        "Desactivado desde Aplicaciones de inicio de Windows. Marca la casilla y guarda para volver a activarlo.",
        "Disabled from Windows Startup apps. Check the box and save to enable it again.");
    public static string StartupStalePath => P(
        "La ruta registrada es la de otro ejecutable. Pulsa Guardar para registrar la ubicación actual.",
        "The registered path belongs to another executable. Press Save to register the current location.");
    public static string BtnUninstall => P("Desinstalar…", "Uninstall…");
    public static string BtnSave => P("Guardar", "Save");
    public static string BtnClose => P("Cerrar", "Close");
    public static string SettingsSaved => P("Configuración guardada.", "Settings saved.");
    public static string SettingsSavedNoKey => P("Configuración guardada. Falta la API key.", "Settings saved. The API key is missing.");
    public static string SettingsSaveFailed => P("No se pudo guardar la configuración.", "Could not save the settings.");

    // ---- Uninstall ----
    public static string UninstallTitle => P($"Desinstalar {AppInfo.Name}", $"Uninstall {AppInfo.Name}");
    public static string UninstallIntro => P(
        "La aplicación se cerrará y se borrará de este equipo. Esta acción no se puede deshacer.",
        "The application will close and be removed from this computer. This cannot be undone.");
    public static string UninstallAllTitle => P("Eliminar todo (completa)", "Remove everything (complete)");
    public static string UninstallAllDesc => P(
        "Borra la clave de OpenAI, configuración, uso, registros, inicio automático y el propio ejecutable.",
        "Deletes the OpenAI key, settings, usage, logs, automatic startup and the executable itself.");
    public static string UninstallAll => P(
        "Eliminar todo: clave, configuración, registros, inicio automático y el propio programa.",
        "Remove everything: key, settings, logs, automatic startup and the program itself.");
    public static string UninstallKeepTitle => P("Conservar mi configuración", "Keep my settings");
    public static string UninstallWingetNote => P(
        "Instalada con winget: también se ejecutará «winget uninstall» para quitar el paquete.",
        "Installed with winget: \"winget uninstall\" will also run to remove the package.");
    public static string UninstallKeepDesc => P(
        "Mantiene atajos, idiomas, vocabulario y opciones para cuando vuelvas a instalar. La clave se elimina siempre por seguridad.",
        "Keeps shortcuts, languages, vocabulary and options for a future reinstall. The key is always removed for security.");
    public static string UninstallKeep => P(
        "Conservar mi configuración (atajo, idiomas, vocabulario, opciones) para una futura reinstalación. La clave se elimina siempre.",
        "Keep my settings (shortcut, languages, vocabulary, options) for a future reinstall. The key is always removed.");
    public static string BtnUninstallConfirm => P("Desinstalar", "Uninstall");
    public static string BtnCancel => P("Cancelar", "Cancel");

    // ---- Usage statistics ----
    public static string HdrUsage => P("Uso y coste", "Usage and cost");
    public static string NoteUsage => P(
        "Cada dictado enviado a OpenAI se anota solo en este equipo (fecha, minutos y coste estimado; nunca el texto, el audio ni la clave).",
        "Every dictation sent to OpenAI is logged only on this computer (date, minutes and estimated cost; never the text, audio or key).");
    public static string BtnStats => P("Estadísticas de uso…", "Usage statistics…");
    public static string StatsTitle => P($"{AppInfo.Name} · Estadísticas de uso", $"{AppInfo.Name} · Usage statistics");
    public static string StatsPeriod => P("Periodo", "Period");
    public static string PeriodToday => P("Hoy", "Today");
    public static string PeriodLast7 => P("Últimos 7 días", "Last 7 days");
    public static string PeriodLast30 => P("Últimos 30 días", "Last 30 days");
    public static string PeriodMonth => P("Este mes", "This month");
    public static string PeriodAll => P("Todo", "All time");
    public static string PeriodCustom => P("Personalizado", "Custom");
    public static string StatsFrom => P("Desde (AAAA-MM-DD)", "From (YYYY-MM-DD)");
    public static string StatsTo => P("Hasta (AAAA-MM-DD)", "To (YYYY-MM-DD)");
    public static string StatsKey => P("Clave de API", "API key");
    public static string KeyAll => P("Todas las claves", "All keys");
    public static string KeyUnknown => P("(sin identificar)", "(unidentified)");
    public static string StatsInvalidDate => P("Fecha no válida: usa AAAA-MM-DD.", "Invalid date: use YYYY-MM-DD.");
    public static string StatsLoadFailed => P("No se pudieron leer las estadísticas.", "The statistics could not be read.");
    public static string StatDictations => P("Dictados", "Dictations");
    public static string StatMinutes => P("Minutos de audio", "Audio minutes");
    public static string StatCost => P("Coste estimado", "Estimated cost");
    public static string StatAverage => P("Coste medio por dictado", "Average cost per dictation");
    public static string StatTokens(string input, string output) => P($"Tokens informados por la API: {input} de entrada · {output} de salida", $"Tokens reported by the API: {input} input · {output} output");
    public static string ChartTitle(string max) => P($"Coste por día (máx. {max})", $"Cost per day (max {max})");
    public static string ByKeyTitle => P("Por clave", "By key");
    public static string NoData => P("No hay dictados registrados con este filtro.", "No dictations recorded with this filter.");
    public static string UsageLine(string count, string minutes, string cost) => P($"{count} dictados · {minutes} min · {cost}", $"{count} dictations · {minutes} min · {cost}");
    public static string StatsNote(string model, string perMinute) => P(
        $"Importe estimado con la tarifa pública de {model} ({perMinute} por minuto de audio): la API no devuelve el coste. Solo cuenta los dictados hechos en este equipo; la cifra oficial está en el panel de uso de OpenAI.",
        $"Amount estimated with the public {model} rate ({perMinute} per audio minute): the API does not return the cost. It only counts dictations made on this computer; the official figure is in the OpenAI usage dashboard.");
    public static string BtnReset => P("Reiniciar…", "Reset…");
    public static string ResetTitle => P("Reiniciar estadísticas", "Reset statistics");
    public static string ResetNothing => P("No hay registros que borrar con este filtro.", "There are no records to delete with this filter.");
    public static string ResetConfirm(int count) => P(
        $"Se borrarán {count} registros del filtro actual (periodo y clave). Esta acción no se puede deshacer.",
        $"{count} records matching the current filter (period and key) will be deleted. This cannot be undone.");
    public static string ResetDone(int count) => P($"Se borraron {count} registros.", $"{count} records deleted.");
    public static string ResetFailed => P("No se pudieron borrar los registros.", "The records could not be deleted.");
}
