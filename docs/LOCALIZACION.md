# Localización (español / inglés)

La interfaz habla **español e inglés**. Por defecto sigue el idioma de **Windows**; el operador puede cambiarlo
en **🛠 Configuración → IDIOMA** y su elección manda a partir de entonces.

## Cómo funciona

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

## Qué NO se traduce, a propósito

- **Los registros (`logs\`)**: son para soporte, y un log en el idioma del cliente complica el diagnóstico.
- **La API REST**: sus respuestas son para automatización, no para leerlas a ojo.
- **El instalador y el manual**: hoy solo en español (ver *Pendiente*).
- Etiquetas técnicas que no se traducen en el sector: `PGM`, `REC`, `FPS OUT`, `BITRATE`, `GOP`, `dBFS`…

## Dónde se guarda la elección

En `data\language.json`, junto a los datos de la aplicación. Si no existe (primer arranque) o no se puede leer,
manda el idioma de Windows.

## Pendiente

- **Instalador** (Inno Setup): hoy va en español. Inno admite varios idiomas con selección al arrancar; sería
  añadir `Languages` y traducir los textos personalizados del asistente.
- **Manual de usuario y LÉEME de FFmpeg**: hoy solo en español. Si vas a vender fuera, conviene una versión en
  inglés de ambos.
