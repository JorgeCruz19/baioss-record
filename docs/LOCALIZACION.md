# Localización (español / inglés)

El producto habla **español e inglés** de principio a fin: el instalador, la aplicación, el contrato, los avisos
de terceros y el manual de usuario. Por defecto todo sigue el idioma de **Windows**; en la aplicación, el
operador puede cambiarlo en **🛠 Configuración → IDIOMA** y su elección manda a partir de entonces.

## Cómo funciona la aplicación

| Pieza | Dónde | Qué hace |
|---|---|---|
| `Strings.cs` | `Baioss.Record.Application/Localization/` | El catálogo: un diccionario por idioma, una línea por cadena |
| `Localizer.cs` | ídem | Idioma vigente, `T()`, `F()`, `Plural()` y detección del idioma del sistema |
| `Loc.cs` | `Baioss.Record.App/Localization/` | Adaptador enlazable para el XAML (indexador + `INotifyPropertyChanged`), persistencia de la elección |
| `TExtension.cs` | ídem | La extensión de marcado `{loc:T Clave}` |

**El cambio es en caliente.** `{loc:T Clave}` no devuelve texto suelto: devuelve un **enlace** al indexador de
`Loc`. Al cambiar de idioma se notifica ese indexador y WPF reevalúa todos los enlaces de golpe, así que la
interfaz cambia sin reiniciar ni interrumpir una grabación.

**Los textos que se componen en C#** (el resumen de la licencia, el estado de la señal, los mensajes de la
programación…) no pasan por enlaces, así que los ViewModels se suscriben a `Localizer.LanguageChanged` y los
recomponen. Los que se derivan del estado del canal simplemente re-aplican el último estado conocido.

**Por qué el catálogo vive en la capa Application** y no en la de interfaz: ahí se generan textos de usuario
—`LicenseInfo.Summary`, por ejemplo— que deben hablar el mismo idioma que la ventana que los muestra. De paso,
se puede probar sin arrastrar WPF (el proyecto de tests es `net8.0`, no `net8.0-windows`).

**Por qué diccionarios en C# y no archivos `.resx`:** con dos idiomas, un diccionario es una línea por cadena en
vez de cuatro de XML, se revisa de un vistazo en el control de versiones, no arrastra ensamblados satélite ni
archivos generados que se desincronizan, y permite el test que garantiza que ambos idiomas estén completos.

## Añadir o cambiar una cadena

1. Añádela en **los dos** diccionarios de `Strings.cs`, con la misma clave.
2. Úsala: en XAML `{loc:T Mi_Clave}`; en C# `Loc.T("Mi_Clave")`, `Loc.F("Mi_Clave", arg)` o
   `Loc.Plural("..._One", "..._Many", n)` para singular/plural.
3. Si el texto lo compone un ViewModel, asegúrate de que ese ViewModel se recompone al cambiar de idioma
   (suscripción a `Localizer.LanguageChanged`).

`LocalizationTests` falla si: falta una clave en algún idioma, los marcadores `{0}` no coinciden entre ambos,
o alguna cadena queda vacía. Es la red que evita publicar una versión a medio traducir.

## Dónde se esconde el texto sin traducir

Los tests garantizan que el **catálogo** esté completo, pero no pueden ver si una pantalla lo usa. Estos tres
sitios se saltan el catálogo sin que nada chille, y los tres han mordido ya:

- **Enlazar un `enum` directamente.** `Text="{Binding RecordingState}"` pinta el `ToString()` del enum —«Idle»,
  «Recording»—, que es el nombre del símbolo en C# y no está en ningún idioma. Se enlaza una propiedad
  calculada que traduzca (`RecordingStateText`), con `[NotifyPropertyChangedFor]` y un aviso explícito en
  `OnLanguageChanged` (si el estado no cambia, `Sync` no la refresca sola).
- **Los `Setter` dentro de un `Style`.** `<Setter Property="Text" Value="Inactivo"/>` no lo detecta ninguna
  búsqueda de `Text="…"`. Ahí `{loc:T Clave}` **sí** vale: `Setter.Value` admite un enlace. (`Trigger.Value`
  no, pero ahí nunca va texto de usuario.)
- **Los valores iniciales de un `[ObservableProperty]`** y las **excepciones cuyo `Message` acaba en pantalla**
  (las de `ChannelHost.RebindAsync` se muestran tal cual en la barra de estado de Entradas).

Un barrido útil antes de dar por buena una pantalla: buscar en los `.cs` de la capa App literales con acentos
o `¿¡` fuera de comentarios y de llamadas a `Serilog`; lo que quede es texto de usuario sin traducir.

## El instalador

El asistente (Inno Setup) también habla los dos idiomas y **elige solo**: sigue el idioma de Windows y solo
pregunta si no es ninguno de los dos (`ShowLanguageDialog=auto`), igual que la aplicación.

| Pieza | Dónde |
|---|---|
| Los dos idiomas y su contrato | `[Languages]` de `installer\baioss-record.iss` (parámetro `LicenseFile` por idioma) |
| Todos los textos propios | `[CustomMessages]`, con prefijo `es.` / `en.` |
| Cómo se leen | `{cm:Clave}` en las secciones; `CustomMessage('Clave')` en el `[Code]` |

Reglas que conviene no romper:

- **Ningún texto propio va escrito «a pelo» en las secciones.** Si añades uno, añádelo en los dos idiomas: si
  falta en uno, **Inno no compila** — que es justo lo que queremos.
- **`%n`** es el salto de línea y **`%1`, `%2`…** los huecos (se rellenan con `FmtMessage`).
- **El `.iss` y los `.txt` van en UTF-8 CON BOM.** Sin BOM, Inno los lee con la página de códigos ANSI del
  sistema y los acentos salen rotos («instalaciÃ³n»).
- **Si cambias el texto de la última página**, recuerda `WizardForm.FinishedLabel.AdjustHeight`: Inno
  dimensiona esa etiqueta para SU texto (61 px) antes de llamarnos, y todo lo que sobre se recorta en silencio.
- Los **botones de los avisos** (`Aceptar`, `Sí`/`No`) los pone Windows en el idioma del sistema, no el
  instalador. Pasa también con los diálogos propios de Inno; no es un fallo de traducción.

## Qué NO se traduce, a propósito

- **Los registros (`logs\`)**: son para soporte, y un log en el idioma del cliente complica el diagnóstico.
- **La API REST**: sus respuestas son para automatización, no para leerlas a ojo.
- **La documentación de `docs\`** (esta incluida): es material interno, no se entrega al cliente. Las dos
  excepciones son el manual de usuario y el LÉEME de FFmpeg, que sí van en los dos idiomas.
- Etiquetas técnicas que no se traducen en el sector: `PGM`, `REC`, `FPS OUT`, `BITRATE`, `GOP`, `dBFS`…

## Dónde se guarda la elección

En `data\language.json`, junto a los datos de la aplicación. Si no existe (primer arranque) o no se puede leer,
manda el idioma de Windows.

## Documentos que hay que mantener a la par

Cada uno tiene su gemelo; si tocas uno, toca el otro:

| Español | Inglés | Dónde acaba |
|---|---|---|
| `installer\EULA.txt` | `installer\EULA-EN.txt` | Se muestra en el asistente y se instala junto al programa |
| `installer\AVISOS-TERCEROS.txt` | `installer\THIRD-PARTY-NOTICES.txt` | Junto al programa |
| `installer\FFMPEG-LEEME.txt` | `installer\FFMPEG-README.txt` | En `tools\ffmpeg\` |
| `docs\MANUAL-USUARIO.md` | `docs\USER-MANUAL.md` | Se entrega al cliente |

Los cuatro pares se instalan **completos, en los dos idiomas**, independientemente del idioma del asistente:
quien instala y quien opera no tienen por qué ser la misma persona, y el idioma de la aplicación se cambia en
caliente (el aviso de «falta FFmpeg» nombra el archivo del idioma en que esté la ventana en ese momento).

> El EULA en inglés lleva una cláusula 9 diciendo que **prevalece la versión española**. Eso es una decisión
> del abogado, no una decisión técnica: en algunas jurisdicciones no se puede vincular a un consumidor con una
> versión en un idioma que no habla. Está puesto para que la pregunta no se olvide.
