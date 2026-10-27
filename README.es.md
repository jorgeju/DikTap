# DikTap

**Dictado por voz para cualquier programa de Windows.** Pulsa un atajo, habla, vuelve a pulsar y el texto transcrito
aparece donde tenías el cursor: el chat, el editor, el navegador... Usa la API de transcripción de OpenAI
(`gpt-transcribe`) con tu propia API key.

[English version](README.md)

## Características

- **Un atajo en todas partes:** `Win+H` (sustituye al dictado de Windows mientras DikTap está abierto) o `Ctrl+Alt+Espacio`. `Esc` cancela.
- **Indicador minimalista** con una onda de audio que reacciona a tu voz, para saber que el micrófono te oye.
- **Seguro al pegar:** solo pega si sigues en la misma ventana; si no, deja el texto en el portapapeles y te avisa. No envía Enter.
- **Ligero:** vive en la bandeja del sistema, ~10 MB de memoria privada y 0 % de CPU en reposo.
- **Portable y sin instalación:** un único `DikTap.exe` (incluye .NET). Interfaz en español e inglés.
- **Actualización automática:** busca e instala nuevas versiones con un solo clic directamente desde Configuración (con verificación de integridad SHA-256 y reinicio automático).
- **Privado:** sin telemetría; no guarda audio ni textos. Tu clave se guarda en el Administrador de credenciales de Windows.
- **Estadísticas de uso y coste estimado** (en local) para saber cuánto gastas en OpenAI.

## Requisitos

- Windows 11 (x64).
- Una [API key de OpenAI](https://platform.openai.com/api-keys) con acceso a la transcripción. El uso se factura a tu cuenta de OpenAI (≈ 0,0045 USD por minuto de audio).

## Instalación

<!-- Descomentar cuando el paquete esté aprobado en winget:
### Con winget

```powershell
winget install jorgeju.DikTap
```
-->

### Descarga manual (ZIP)

1. Descarga `DikTap-<versión>-win-x64.zip` desde [Releases](https://github.com/jorgeju/DikTap/releases/latest).
2. Descomprímelo en una carpeta estable (por ejemplo `C:\Herramientas\DikTap`) y ejecuta `DikTap.exe`.
3. Se abre la **Configuración**: pega tu API key y pulsa *Guardar*. Por defecto se inicia con Windows (puedes desmarcarlo).

> DikTap no está firmado digitalmente, así que Windows SmartScreen puede avisar la primera vez
> («Más información» → «Ejecutar de todas formas»). Ejecútalo solo si lo has descargado de este repositorio; el SHA-256 de cada versión está en su página de Releases.

## Uso

1. Coloca el cursor donde quieras escribir y pulsa `Win+H`.
2. Habla mientras la onda se mueve. Pulsa `Win+H` de nuevo para terminar.
3. El texto se transcribe y se pega. `Esc` cancela en cualquier momento.

Doble clic en el icono de la bandeja (o abrir de nuevo el EXE) abre la Configuración.

## Configuración

| Opción | Qué hace |
|---|---|
| API key | Se guarda cifrada en el Administrador de credenciales de Windows; nunca se muestra. |
| Micrófono y atajo | Dispositivo concreto o el predeterminado; `Win+H` o `Ctrl+Alt+Espacio`. |
| Idiomas, vocabulario y contexto | Pistas opcionales para mejorar el reconocimiento (nombres propios, jerga técnica...). |
| Portapapeles tras pegar | Restaurar lo que tenías (por defecto), dejar el dictado o dejarlo también en el historial `Win+V`. |
| Estadísticas de uso | Dictados, minutos y coste estimado por periodo y por clave. Los datos solo se guardan en tu equipo. |
| Idioma y actualizaciones | Automático (el de Windows), español o inglés; búsqueda e instalación de actualizaciones desde GitHub. |

## Privacidad

- El audio se envía **directamente** a `api.openai.com` por HTTPS, solo mientras dictas, y DikTap no lo guarda en ningún sitio. No hay servidor intermedio ni telemetría.
- Cada persona usa su propia API key y su propia facturación; DikTap no usa tu sesión ni suscripción de ChatGPT.
- Ajustes en `%LOCALAPPDATA%\DikTap`. El registro técnico no contiene claves, audio ni texto dictado.

## Limitaciones

- No pega en programas ejecutados como administrador (Windows lo impide): el texto queda en el portapapeles.
- `Win+H` no se intercepta si el programa en primer plano está elevado ni en la pantalla de bloqueo.
- No detecta campos de contraseña: eliges tú el destino.
- Una grabación dura como máximo 10 minutos.
- Si se desconecta el micrófono durante el dictado, se transcribe y pega lo grabado hasta ese momento.

## Desinstalar

*Configuración → Desinstalar…* elimina la clave, el inicio automático, los datos y el propio programa (con la opción de conservar tus ajustes).
<!-- Descomentar junto con el bloque de winget: si lo instalaste con winget, el mismo botón ejecuta también `winget uninstall jorgeju.DikTap`, que también puedes ejecutar tú. -->

## Compilar desde el código

Necesitas el SDK de .NET 10 (sin Visual Studio).

```powershell
./scripts/Build.ps1                # compila y ejecuta las pruebas
./scripts/Publish.ps1 -Package     # EXE autocontenido y ZIP en artifacts/
```

## Contribuir

¡Las contribuciones son bienvenidas! Puedes [abrir un issue](https://github.com/jorgeju/DikTap/issues/new/choose) con un error o una idea,
o enviar una *pull request*. Lee antes [CONTRIBUTING.md](CONTRIBUTING.md). Todos los cambios pasan por revisión del mantenedor.

**Enfoque actual:** DikTap quiere ser ultraligero y rápido, con pocas opciones. Cualquier propuesta es bienvenida, también las que añadirían peso o complejidad:
se valoran todas. Pero no todo se incluirá, por bien que funcione, si no encaja con esa filosofía (que podría evolucionar en el futuro). Antes de programar algo grande, abre un issue para comentarlo.

## Licencia

DikTap es software libre bajo la [GNU GPL v3.0 (solo v3)](LICENSE): puedes usarlo gratis, estudiarlo, modificarlo y compartirlo. Si distribuyes una versión modificada,
debe seguir bajo GPL-3.0 con su código fuente disponible. Se ofrece sin garantía.

**Nombre e icono:** la licencia cubre el código, no concede derechos sobre el nombre «DikTap» ni su icono. Si publicas una versión modificada, usa otro nombre e icono para no confundirla con la original.
DikTap no está afiliado a OpenAI ni a Microsoft. Avisos de terceros en [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
