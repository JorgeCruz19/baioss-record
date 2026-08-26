# Instalador de Baioss Record

Genera un **único `.exe`** con asistente por pasos, en español, que pregunta al cliente si quiere el **periodo de prueba de 14 días** o **activar una licencia**.

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

---

## Qué ve el cliente

1. **Bienvenida**
2. **Acuerdo de licencia** (`installer\EULA.txt` — conviene que lo revise un abogado antes de vender)
3. **Carpeta de destino** — por defecto `C:\Baioss\Record`
4. **Accesos directos y arranque** — dos casillas: acceso en el escritorio e *«Iniciar Baioss Record al encender el equipo»*
5. **Canales de grabación** — cuántos canales quiere el cliente (1 a 4; por defecto 4, y al actualizar se preselecciona lo ya instalado). La aplicación muestra exactamente esos canales.
6. **Tipo de instalación**
   - **Periodo de prueba de 14 días** (opción por defecto)
   - **Ya tengo una licencia para este equipo** → aparece un campo para pegarla
7. **Resumen** (incluye los canales elegidos) e instalación
8. **Fin** — si eligió la prueba, se le muestra **el código de este equipo**, que es justo lo que necesita enviarte para que le emitas la licencia

---

## Decisiones que conviene conocer

**No se instala en «Archivos de programa».** La aplicación escribe su base de datos, los registros y (por defecto) las grabaciones **junto a su ejecutable**, y corre sin privilegios de administrador. Dentro de `Archivos de programa` Windows se lo impediría y el producto no podría ni grabar. Por eso el destino es `C:\Baioss\Record` y además se concede permiso de escritura al grupo *Usuarios* sobre esa carpeta.

**El arranque automático es para todos los usuarios.** Como la instalación corre elevada, usar la carpeta de Inicio *del usuario* apuntaría a la del administrador que instala, no a la del operador que después usa el equipo — y el arranque automático simplemente no ocurriría.

**La licencia introducida en el asistente no se valida allí.** El instalador la deja preparada y es la **aplicación** quien la comprueba y la guarda en su primer arranque. El motivo: el estado de licencia va firmado con una clave derivada de la huella del equipo, y duplicar ese cálculo en el instalador sería una fuente segura de errores. Si la clave no fuese válida, el programa queda en periodo de prueba y el operador puede reintroducirla desde la ventana **Licencia**.

**El código de equipo lo calcula la propia aplicación** (`--machine-code`), no el instalador. Así es imposible que difieran: si lo hicieran, las licencias emitidas no validarían.

**Al desinstalar no se borran las grabaciones ni la base de datos.** Son material del cliente. Solo se retira el programa y sus registros.

**El instalador crea una clave de registro del equipo (`HKLM\Software\Baioss\Record`) con permiso de escritura para Usuarios.** Es la tercera copia del estado de licencia/prueba, compartida por todas las cuentas del PC: sin ella, bastaba borrar el archivo de `ProgramData` y entrar con otra cuenta de Windows para reiniciar el periodo de prueba. No se borra al desinstalar a propósito — desinstalar y reinstalar no reinicia la prueba.

**Los canales elegidos van en `HKLM\Software\Baioss\RecordSetup` (clave HERMANA de la anterior, a propósito).** El número de canales no vive en un archivo junto al exe (el operador podría subírselo editándolo) sino en una clave que conserva la ACL por defecto de HKLM: los usuarios la leen y solo un administrador la cambia — es decir, **cambiar de canales = volver a ejecutar el instalador**. No puede anidarse dentro de `Software\Baioss\Record` porque esa clave concede `users-modify` y los permisos del registro se heredan a las subclaves. Sin la clave (desarrollo/portable), la app usa el número incrustado en el binario, como siempre. Si se reduce el número de canales, los datos de los canales sobrantes (grabaciones, programaciones) no se borran: quedan inactivos hasta que se vuelva a instalar con más canales.

**No se empaquetan datos de desarrollo.** La carpeta `publish\` es también la que se usa para probar en local, así que el instalador excluye expresamente `data\`, `logs\` y `recordings\`; sin eso, la base de datos de pruebas y los registros del desarrollador acabarían en el equipo del cliente.

---

## Antes de vender: firma digital

El instalador **no está firmado**. Sin firma, Windows SmartScreen mostrará al cliente una advertencia de «editor desconocido», que en una venta profesional resta mucha confianza.

Necesitas un **certificado de firma de código** (OV o EV) de una autoridad reconocida y firmar tanto el `.exe` de la aplicación como el instalador:

```bash
signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 /f certificado.pfx /p CONTRASEÑA dist\BaiossRecord-1.0.0-Setup.exe
```

Un certificado **EV** además evita el periodo inicial de «reputación» de SmartScreen, así que las primeras descargas ya no salen marcadas.

---

## Actualizar a una versión nueva

Instalar encima sustituye los archivos del programa y **respeta** la base de datos, la configuración, las grabaciones y la licencia (que vive en `%ProgramData%\Baioss\Record`). El `AppId` del script es el que identifica al producto: **no lo cambies** entre versiones, o Windows tratará la nueva como un producto distinto y quedarían dos instalaciones.
