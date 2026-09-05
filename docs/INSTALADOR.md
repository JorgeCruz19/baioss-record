# Instalador de Baioss Record

Genera un **único `.exe`** con asistente por pasos —**en español o en inglés**, según el idioma de Windows— que pregunta al cliente si quiere el **periodo de prueba de 14 días** o **activar una licencia**.

---

## Generarlo

Una sola vez, instala la herramienta:

```bash
winget install --id JRSoftware.InnoSetup
```

Y ya, cada vez que quieras publicar:

```bash
powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1
```

El script publica la app (self-contained: el equipo del cliente **no necesita instalar .NET ni nada**) y compila el instalador en `dist\BaiossRecord-<versión>-Setup.exe`.

Si solo estás retocando el instalador y no el programa, reutiliza la publicación anterior:

```bash
powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1 -SkipPublish -Version 1.0.1
```

**Para la build que vas a VENDER, añade `-Obfuscate`** (cifra las cadenas y renombra los internos de la lógica sensible, licenciamiento incluido; ver `docs\LICENCIAMIENTO.md` §6):

```bash
powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1 -Obfuscate
```

---

## Qué ve el cliente

1. **Bienvenida**
2. **Acuerdo de licencia** (`installer\EULA.txt` en español, `installer\EULA-EN.txt` en inglés — conviene que un abogado revise **los dos** antes de vender)
3. **Carpeta de destino** — por defecto `C:\Baioss\Record`
4. **Accesos directos y arranque** — dos casillas: acceso en el escritorio e *«Iniciar Baioss Record al encender el equipo»*
5. **Canales de grabación** — cuántos canales quiere el cliente (1 a 4; por defecto 4, y al actualizar se preselecciona lo ya instalado). La aplicación muestra exactamente esos canales. **Lo que elige aquí es una preferencia, no lo que compró**: al activar una licencia, los canales *pagados* (que viajan firmados dentro de la clave) acotan lo elegido — mín(elegido, licenciado). En periodo de prueba se respeta lo elegido.
6. **Tipo de instalación**
   - **Periodo de prueba de 14 días** (opción por defecto)
   - **Ya tengo una licencia para este equipo** → aparece un campo para pegarla
7. **Resumen** (incluye los canales elegidos) e instalación
8. **Fin** — se le indica que **falta copiar FFmpeg** (con la ruta exacta) y, si eligió la prueba, **el código de este equipo**, que es lo que necesita enviarte para que le emitas la licencia. Una casilla marcada le abre la carpeta de FFmpeg en el Explorador

---

## Decisiones que conviene conocer

**El asistente habla español e inglés, y elige solo.** Sigue el idioma de Windows (cualquier variante de español → español; cualquier otro → inglés), igual que la aplicación; **solo pregunta** si Windows no es ninguno de los dos (`ShowLanguageDialog=auto`). Ningún texto propio va escrito «a pelo» en las secciones del `.iss`: todos viven en `[CustomMessages]` con prefijo `es.` / `en.` y se leen con `{cm:Clave}` o, en el `[Code]`, con `CustomMessage('Clave')`. Si añades un texto y olvidas un idioma, **Inno no compila**. El contrato lo aporta cada idioma por su cuenta (parámetro `LicenseFile` de `[Languages]`).

Dos detalles que conviene no perder de vista:

- **El `.iss` se guarda en UTF-8 CON BOM.** Sin el BOM, Inno lo lee con la página de códigos ANSI del sistema y los acentos del asistente salen rotos («instalaciÃ³n»). Lo mismo vale para los `.txt` que se muestran o se instalan.
- **Los botones de los avisos (`Aceptar`, `Sí`/`No`) los pone Windows**, no el instalador: salen en el idioma del sistema aunque el asistente vaya en el otro. Es así también en los diálogos propios de Inno; no es un fallo de traducción.

**No se instala en «Archivos de programa».** La aplicación escribe su base de datos, los registros y (por defecto) las grabaciones **junto a su ejecutable**, y corre sin privilegios de administrador. Dentro de `Archivos de programa` Windows se lo impediría y el producto no podría ni grabar. Por eso el destino es `C:\Baioss\Record` y además se concede permiso de escritura al grupo *Usuarios* sobre esa carpeta.

**El arranque automático es para todos los usuarios.** Como la instalación corre elevada, usar la carpeta de Inicio *del usuario* apuntaría a la del administrador que instala, no a la del operador que después usa el equipo — y el arranque automático simplemente no ocurriría.

**La licencia introducida en el asistente no se valida allí.** El instalador la deja preparada y es la **aplicación** quien la comprueba y la guarda en su primer arranque. El motivo: el estado de licencia va firmado con una clave derivada de la huella del equipo, y duplicar ese cálculo en el instalador sería una fuente segura de errores. Si la clave no fuese válida, el programa queda en periodo de prueba y el operador puede reintroducirla desde la ventana **Licencia**.

**El código de equipo lo calcula la propia aplicación** (`--machine-code`), no el instalador. Así es imposible que difieran: si lo hicieran, las licencias emitidas no validarían.

**Al desinstalar no se borran las grabaciones ni la base de datos.** Son material del cliente. Solo se retira el programa y sus registros.

**El instalador crea una clave de registro del equipo (`HKLM\Software\Baioss\Record`) con permiso de escritura para Usuarios.** Es la tercera copia del estado de licencia/prueba, compartida por todas las cuentas del PC: sin ella, bastaba borrar el archivo de `ProgramData` y entrar con otra cuenta de Windows para reiniciar el periodo de prueba. No se borra al desinstalar a propósito — desinstalar y reinstalar no reinicia la prueba.

**Los canales elegidos van en `HKLM\Software\Baioss\RecordSetup` (clave HERMANA de la anterior, a propósito).** El número de canales no vive en un archivo junto al exe (el operador podría subírselo editándolo) sino en una clave que conserva la ACL por defecto de HKLM: los usuarios la leen y solo un administrador la cambia — es decir, **cambiar de canales = volver a ejecutar el instalador**. No puede anidarse dentro de `Software\Baioss\Record` porque esa clave concede `users-modify` y los permisos del registro se heredan a las subclaves. Sin la clave (desarrollo/portable), la app usa el número incrustado en el binario, como siempre. Si se reduce el número de canales, los datos de los canales sobrantes (grabaciones, programaciones) no se borran: quedan inactivos hasta que se vuelva a instalar con más canales.

**FFmpeg NO se empaqueta.** Su licencia no permite redistribuirlo (es un build *nonfree*, ver `FFMPEG.md`): el instalador crea `tools\ffmpeg\` con permiso de escritura y las instrucciones en los dos idiomas (`FFMPEG-LEEME.txt` y `FFMPEG-README.txt`), y es el cliente quien copia ahí `ffmpeg.exe` y `ffprobe.exe`. Por eso el instalador pesa ~62 MB en vez de ~135 MB. Mientras falte, la aplicación abre en modo de demostración y **avisa con un diálogo** en cada arranque.

**Los textos legales y las instrucciones se instalan en los DOS idiomas**, no solo en el del asistente: `EULA.txt` / `EULA-EN.txt` y `AVISOS-TERCEROS.txt` / `THIRD-PARTY-NOTICES.txt` junto al programa, y los dos LÉEME de FFmpeg dentro de `tools\ffmpeg\`. El motivo es práctico: quien instala (informática) y quien luego opera el equipo no tienen por qué ser la misma persona, y **el idioma de la aplicación se cambia en caliente** — el aviso de «falta FFmpeg» nombra el archivo del idioma en que esté la ventana en ese momento, así que los dos tienen que existir.

**No se empaquetan datos de desarrollo.** La carpeta `publish\` es también la que se usa para probar en local, así que el instalador excluye expresamente `data\`, `logs\` y `recordings\`; sin eso, la base de datos de pruebas y los registros del desarrollador acabarían en el equipo del cliente.

---

## Antes de vender

> **Lee primero `CHECKLIST-VENTA.md`**: recoge TODO lo que falta para poder cobrar por el producto, incluido un
> bloqueante legal importante (el FFmpeg que se empaqueta hoy **no es redistribuible**).

### Firma digital

El instalador **no está firmado**. Sin firma, Windows SmartScreen mostrará al cliente una advertencia de «editor desconocido», que en una venta profesional resta mucha confianza.

Necesitas un **certificado de firma de código** (OV o EV) de una autoridad reconocida y firmar tanto el `.exe` de la aplicación como el instalador:

```bash
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /f certificado.pfx /p CONTRASEÑA dist\BaiossRecord-1.0.0-Setup.exe
```

Un certificado **EV** además evita el periodo inicial de «reputación» de SmartScreen, así que las primeras descargas ya no salen marcadas.

---

## Actualizar a una versión nueva

Instalar encima sustituye los archivos del programa y **respeta** la base de datos, la configuración, las grabaciones y la licencia (que vive en `%ProgramData%\Baioss\Record`). El `AppId` del script es el que identifica al producto: **no lo cambies** entre versiones, o Windows tratará la nueva como un producto distinto y quedarían dos instalaciones.
