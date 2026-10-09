# Licenciamiento — Baioss Record

Periodo de prueba de **14 días** y licencia **perpetua atada a un equipo**. Todo funciona **sin conexión**: no hay servidor de licencias ni activación por internet.

---

> Para lo que falta antes de la primera venta (custodia de la clave privada, registro de licencias emitidas,
> EULA, firma digital y un bloqueante legal con FFmpeg), ver **`CHECKLIST-VENTA.md`**.

## 1. Puesta en marcha (una sola vez, tú como proveedor)

Genera tu par de claves:

```bash
dotnet run --project tools/Baioss.Record.LicenseTool -- keygen
```

Imprime dos claves:

- **PRIVADA** → guárdala fuera del repositorio (gestor de contraseñas o similar). Con ella emites licencias. **Si se filtra, cualquiera puede emitir licencias gratis; si se pierde, hay que publicar una versión nueva con otra clave pública.**
- **PÚBLICA** → pégala en `src/Baioss.Record.Application/Licensing/LicensePublicKey.cs` y recompila.

> Mientras la clave pública esté vacía, la app **no acepta ninguna licencia** (solo funciona el periodo de prueba). Es el comportamiento seguro por defecto.

**No se lee de configuración a propósito:** si viniera de `appsettings.json` o de una variable de entorno, cualquiera podría sustituirla por la suya y emitirse licencias.

---

## 2. Vender una licencia (por cada cliente)

1. El cliente abre **🔑 Licencia** en la app y te envía su **código de equipo** (p. ej. `D68J-FZNH-E2JV-XY2E`).
2. Emites la licencia con los canales que **pagó** (sin `--channels`, se emite de 4):

```bash
dotnet run --project tools/Baioss.Record.LicenseTool -- issue --machine D68J-FZNH-E2JV-XY2E --channels 2 --private <TU-CLAVE-PRIVADA>
```

3. Le envías la clave resultante. Él la pega en la misma ventana y pulsa **Activar**.

**Los canales pagados viajan FIRMADOS dentro de la clave** — el precio del producto depende de ellos, así que el techo no puede vivir en nada editable. La regla: canales efectivos = *mín(lo elegido en el instalador, lo licenciado)*. Durante la **prueba** mandan los del instalador (el cliente evalúa lo que quiera ver, hasta 4); al **activar**, lo pagado acota — si eligió 4 y pagó 2, verá 2 y la pastilla dirá «Licencia activa · 2 canales». **Al activar, la app avisa y se reinicia sola** (con el apagado ordenado: una grabación en curso se finaliza correctamente antes de cerrar) para que los canales comprados aparezcan al momento. **Ampliar canales = emitir una clave nueva** para el mismo equipo con más `--channels` (se paga la diferencia y el cliente pega la clave nueva en la ventana Licencia; sin reinstalar). Si la licencia no es verificable (fallo de E/S), NO se recorta nada: fail-open, como todo el subsistema.

La licencia **solo funciona en ese equipo**: la huella del PC forma parte de lo que se firma, así que en otro ordenador la firma no valida.

> La herramienta es estricta con el código de equipo: si le faltan o sobran caracteres **se niega a emitir** (antes firmaba en silencio una licencia que jamás validaría en el equipo del cliente). También se niega si el código corresponde a un equipo **sin identificadores utilizables** — esa huella es genérica y una licencia emitida para ella funcionaría en cualquier equipo en ese estado.
>
> La herramienta muestra además el **ancla** del código: «chip TPM del equipo» (una copia del disco no valida la licencia) o «identificadores clásicos, SIN TPM», con un aviso. En el segundo caso puedes pedir al cliente que active el TPM en la BIOS y te envíe el código nuevo; una licencia emitida para el código clásico deja de valer si el TPM se activa después (la huella cambia de nivel). Los códigos son los de la huella v2: los que mostraban las builds anteriores a 2026-10 ya no sirven.

---

## 3. Qué pasa cuando termina la prueba

| | Durante la prueba | Prueba terminada |
|---|---|---|
| Ver las entradas (preview) | ✅ | ✅ |
| **Iniciar** una grabación | ✅ | ❌ bloqueado |
| Grabación **ya en curso** | ✅ | ✅ **nunca se interrumpe** |
| Detener, renombrar, gestionar | ✅ | ✅ |
| Programación | ✅ | Las franjas **no se pierden**: se reintentan mientras duren, así que si activas a mitad se graba el resto |

**Decisión de diseño deliberada:** en un grabador 24/7 cortar una emisión en marcha sería mucho peor que un impago. La licencia **solo** impide *empezar* a grabar; ni bloquea el arranque de la app, ni muestra ventanas modales, ni detiene nada que ya esté grabando.

---

## 4. Cómo está construido (y por qué)

**Firma asimétrica (ECDSA P-256), no una contraseña ni un HMAC.** La app solo lleva la clave **pública**: sirve para *verificar*, no para *emitir*. Aunque alguien descompile el programa, no puede fabricarse licencias.

**La huella del equipo se firma pero no viaja en la clave.** Al activar, la app verifica usando *su propia* huella. Copiar la licencia a otro PC produce un mensaje distinto y la firma falla — sin necesidad de consultar a ningún servidor.

**La huella tiene dos niveles (v2, 2026-10).** Si el equipo tiene un chip **TPM 2.0** (obligatorio en Windows 11; en placas de integrador suele ir como firmware, Intel PTT o AMD fTPM, y a veces hay que activarlo en la BIOS), la huella es **solo la clave de endoso del chip**, que el fabricante graba en él y no vive en el disco: una copia del disco arranca en otro chip y obtiene otra huella. Se lee **directamente del chip** (`TPM2_ReadPublic` por el servicio base de TPM de Windows, sin administrador, en torno a 1 ms), no de la copia que Windows guarda en el registro, que un clon se llevaría. Esa huella además sobrevive a reinstalar Windows, cambiar el disco, *Sysprep* y borrar el TPM; solo cambia al cambiar la placa. Si no hay TPM utilizable, la huella es la **clásica**: MachineGuid de Windows, número de serie del volumen del sistema, seriales de placa y de equipo y UUID de sistema del SMBIOS, leídos del **registro y de API nativas, nunca por WMI** (una consulta WMI puede tardar segundos o fallar al arrancar, y de esto depende poder grabar). El nivel viaja en el propio código de equipo (un bit), así que la herramienta de emisión lo muestra y la ventana de Licencia se lo dice al cliente.

**El código de equipo y la licencia se leen en voz alta.** Usan un alfabeto sin caracteres confundibles (nada de `I`, `L`, `O`, `U`) y al teclear se aceptan minúsculas, espacios y las confusiones típicas (`O`→`0`, `I`→`1`).

**El periodo de prueba resiste juegos con el reloj.** Se guarda la fecha de inicio, una marca de agua (el máximo momento visto, que nunca baja) y el **uso real acumulado medido con reloj monotónico**. Atrasar la hora no alarga la prueba; adelantarla y devolverla tampoco la congela. Un ajuste horario legítimo (NTP, cambio de zona) no resta días. Y un reloj **accidentalmente puesto en el futuro** (pila CMOS, año mal tecleado) tampoco quema la prueba: la marca de agua no puede avanzar más deprisa que el tiempo real medido, así que solo se consume lo que de verdad pasó.

**El estado vive en `%ProgramData%\Baioss\Record\license.dat`**, con copia en el registro del **equipo** (HKLM, la crea el instalador y la comparte todo usuario del PC) y en el del **usuario** (HKCU). Fuera de la carpeta del programa a propósito: actualizar el producto suele reemplazarla y se llevaría por delante la marca de la prueba. Al leer se toma **la fecha más antigua** encontrada, así que borrar una copia no reinicia nada — y la copia HKLM cubre además el «borro el archivo y entro con otra cuenta de Windows». Todas las copias van firmadas con una clave derivada de la huella: una marca copiada de otro PC no cuela.

**Si el estado guardado no valida, la clave se conserva.** Un fallo transitorio al leer un identificador del equipo puede cambiar la huella una sesión (y con ella la firma del estado). En ese caso se reinician las marcas de la prueba, pero la **clave de licencia se rescata** y se re-verifica: el cliente legítimo no pierde su activación por un hipo de E/S. Y si el estado directamente **no se puede leer** (archivo bloqueado por el antivirus o la copia de seguridad), no se toca nada: se queda en «no verificable» y se reintenta al minuto.

**Activar solo confirma si de verdad quedó guardado.** Si la clave es válida pero no se pudo escribir en ninguna ubicación (permisos rotos), el botón lo dice claramente en vez de fingir éxito — y la licencia dejada por el instalador se conserva para reintentar en el próximo arranque.

**Nunca se guarda un «ya activado».** Del disco solo sale el *texto* de la clave; que la licencia sea válida se decide **verificando la firma en cada arranque**. Si se guardara una bandera, bastaría editarla.

**A prueba de fallos propios.** Si no se puede leer el estado (permisos, archivo corrupto, error interno), el estado es «no verificable» y **no se bloquea nada**: se avisa por registro y se sigue grabando.

---

## 5. Límites conocidos

**Clonado de disco.** En un equipo con TPM 2.0 la huella es la clave de endoso del chip, así que una copia del disco en otro equipo NO valida la licencia (tampoco apagando el TPM en la copia: una huella clásica nunca coincide con una anclada al TPM). El límite queda en los equipos **sin TPM**: ahí la huella es la clásica, y si además el equipo no tiene números de serie ni UUID reales en el firmware (PC de integrador con valores de relleno), una copia de su disco en otro equipo igual de anónimo comparte huella y licencia. La herramienta de emisión avisa cuando el código es de un equipo sin TPM, para que puedas pedir que lo activen antes de entregar la licencia; y el registro de a qué código se emitió cada licencia sigue siendo la segunda línea. Otros límites del anclaje: una máquina virtual clonada con su TPM virtual conserva la huella; y si el chip cambia (placa nueva o, en algunos equipos, una actualización de BIOS que reinicia el TPM de firmware) hay que reemitir la licencia, igual que antes al reinstalar Windows. El cruce entre dos equipos físicos no se ha podido probar desde un solo equipo: conviene hacerlo una vez antes de vender (ver §8).

**Parcheo del binario.** Alguien con conocimientos puede descompilar la app (.NET produce C# muy legible con herramientas gratuitas), anular la verificación o cambiarse el tope de canales, y recompilar. Fuera del modelo de amenaza desde el día uno: la defensa real exigiría activación online. La build que se distribuye va **ofuscada** (ver abajo), lo que sube mucho el coste de leer el código descompilado, pero no lo hace imposible. Mientras el negocio sea de pocos clientes con trato directo, el registro de licencias emitidas es la mitigación que más rinde.

## 6. Ofuscación de la build distribuible

El instalador de venta se genera con la opción de **ofuscación** activada:

```bash
powershell -ExecutionPolicy Bypass -File scripts\build-installer.ps1 -Obfuscate
```

Qué hace (Obfuscar, open source, como *dotnet tool* local — se restaura solo con `dotnet tool restore`):

- **Cifra las cadenas** de los ensamblados con lógica sensible. Es la medida de más valor: sin ella, buscar `license`, `trial` o la clave pública Base64 en el DLL las encuentra al instante; con ella, no aparecen en claro. El propio script lo **verifica** (falla ruidosamente si el separador de dominio de la licencia sigue visible).
- **Renombra** métodos y campos privados/internos a nombres sin sentido.

Qué **no** se ofusca, y por qué: solo se procesan `Application`, `Infrastructure` y `Engine.FFmpeg`. La app WPF (`Baioss.Record.App`) y el `Domain` quedan intactos porque WPF resuelve los enlaces del XAML **por nombre** y EF Core mapea las entidades **por nombre**; renombrarlos rompería el producto. Por el mismo motivo se **conserva la API pública** (las clases de licencia son `public` para cruzar ensamblados) y **no se renombran las propiedades** (las usan EF, el JSON de la API y los enlaces del XAML). La build ofuscada se publica **sin ReadyToRun** (el código nativo precompilado chocaría con el IL reescrito; solo se pierde algo de velocidad en el primer arranque). Config en `build\obfuscar.xml`; el paso lo ejecuta `scripts\obfuscate.ps1`. Sin `-Obfuscate`, el flujo normal de desarrollo no ofusca nada.

---

## 7. Comprobar el estado sin abrir la interfaz

```bash
curl http://127.0.0.1:5005/api/v1/license
```

Devuelve el estado, los días restantes y el código de equipo. También existe `POST /api/v1/license/activate` con `{"key":"..."}` (responde **422** si la clave no es válida, nunca un error de servidor).

## 8. Licencia atada al TPM (implementado el 2026-10-03)

**Qué cambió.** Huella v2 en dos niveles (§4). Piezas: `MachineFingerprintComposer` (composición y bit de nivel), `TpmEndorsementKey` (lee la clave de endoso del chip por TBS), `SmbiosSystemUuid` (UUID del firmware para el nivel clásico), `WindowsMachineFingerprint` (elige el nivel y lo deja en el registro de la aplicación), la ventana de Licencia (dice a qué está atado el código), `GET /api/v1/license` (campo `tpmAnchored`) y la herramienta de emisión (línea «Ancla» y aviso sin TPM). El formato de la clave de licencia no cambia.

**Migración.** La huella de TODOS los equipos cambia: las licencias emitidas con builds anteriores dejan de validar y hay que reemitirlas con el código nuevo. Al actualizar un equipo la aplicación NO bloquea de golpe: el estado guardado deja de cuadrar con la huella, la prueba de 14 días empieza de nuevo conservando la clave antigua (que ya no valida), y durante ese plazo se graba con normalidad. No hay licencias de clientes emitidas todavía; solo las de prueba propias.

**Medido con el prototipo** (`prototypes/TpmLicenseProbe`, portátil con Intel PTT y Windows 11, sesión SIN elevación, como corre la aplicación):

| Prueba | Resultado | Coste medido |
|---|---|---|
| Leer la clave de endoso directamente del chip (TBS, `TPM2_ReadPublic` sobre 0x81010001) | Sin administrador; área pública de 314 bytes (RSA-2048); la misma clave que reporta el proveedor de plataforma de Windows | p50 0,9 ms (la primera, 16 ms) |
| Copia de la clave de endoso en el registro (`…\Services\TPM\WMI\Endorsement\EkPub`) | Existe (283 bytes): vive en el disco y un clon se la llevaría. Por eso se lee del chip y no de Windows | |
| UUID de sistema del SMBIOS (`GetSystemFirmwareTable`, sin WMI) | Real en este equipo; firmware, no disco | 4 ms |
| Clave ECDSA P-256 no exportable en el TPM, de usuario | Crear 300 ms; abrir 7 ms; la privada no se exporta; otro proceso la abre y firma en 210 ms | Firma p50 141 ms, verificación 0,4 ms |
| Clave de máquina en el TPM | Crearla exige elevación (NTE_PERM 0x80090010). Creada elevada y con permiso «Usuarios: lectura/uso», un proceso normal la abre y firma; con los permisos por defecto, acceso denegado | Firma 201 ms |

**Por qué basta la identidad del chip, sin una firma del chip.** Una clave propia en el TPM con reto y firma solo añadiría defensa contra una respuesta falsificada del propio sistema, que exige administrador y el mismo esfuerzo que parchear la aplicación: queda fuera del modelo de amenaza, como el parcheo. A cambio costaría provisionar una clave de máquina desde el instalador y 150–200 ms por comprobación. El prototipo deja medida esa vía por si algún día se quiere.

**Fallos de lectura.** «No hay TPM» y «el chip no tiene la clave en su sitio» son definitivos y llevan al nivel clásico, con un aviso en el registro que dice por qué. Un fallo que puede ser pasajero (el servicio de TPM aún arrancando) se reintenta unos segundos al iniciar. No hay vía de «si falla, se da por bueno»: quien clonara un disco podría provocar el fallo a propósito. Si en un equipo licenciado con TPM la lectura fallara una sesión entera, esa sesión vería la licencia como de otro equipo (prueba reiniciada, sin bloqueo inmediato) hasta el siguiente arranque sano.

**Verificado.** 780 pruebas unitarias en verde (37 del licenciamiento; 21 añadidas el 2026-10-08 al revisar qué quedaba sin cubrir) y 100 de integración (95 en verde, 5 omitidas por diseño).
- *Huella:* los niveles nunca se confunden y viajan en el código de equipo; una licencia emitida para un chip se rechaza en otro chip y con el TPM apagado; las respuestas del chip y las tablas de firmware mal formadas (viejas sin UUID, cortadas, corruptas) nunca se toman por un identificador; los DOS niveles del lector real se ejercitan en cualquier equipo con un chip simulado (también el clásico, que este portátil no recorre por sí solo, y el caso de un lector que lanza); el bucle de reintentos solo reintenta fallos pasajeros, agota su presupuesto de 3,75 s y trata la falta de `tbs.dll` como definitiva.
- *Almacén y servicio* (con archivo y rama del registro propios de cada prueba y una clave de proveedor de usar y tirar): primer arranque; estado atado a la huella con rescate de la clave; la migración (prueba nueva sin bloqueo y la clave vuelve a valer cuando vuelve la huella); borrar o manipular una copia no reinicia la prueba y manda la copia más estricta; lectura imposible → `Unknown` sin bloquear ni sembrar; caducidad que solo frena grabaciones nuevas y nombra el código; uso medido con reloj monotónico; activación (otra máquina, basura, otro emisor, sin poder guardar, y sobre un equipo caducado desbloquea al instante).
- *API:* `tpmAnchored` según el código y 422 con el motivo ante una clave rechazada.
- *Build OFUSCADA, en una copia aislada (2026-10-08):* la huella sale anclada al TPM con el mismo código (las funciones nativas se siguen encontrando tras ofuscar), `GET /api/v1/license` devuelve `tpmAnchored: true`, la ventana de Licencia muestra «Protegido contra copias del disco…», la migración se comporta como se describe arriba (las tres copias del estado dejan de cuadrar y la prueba se reinicia conservando la clave), el cierre es ordenado y el registro queda sin errores. La herramienta de emisión reconoce el código como anclado al TPM, avisa ante un código clásico y rechaza un código incompleto.

**Costuras para las pruebas.** `WindowsMachineFingerprint`, `TpmEndorsementKey.TryRead`, `LicenseStore` y `LicenseService` tienen un constructor o una sobrecarga `internal` (lector de la clave de endoso simulado; intento de lectura simulado; archivo y rama del registro propios; clave pública de usar y tirar) que solo ve el proyecto de pruebas. La configuración de producción no cambia: el constructor público delega con los valores reales.

**Pendiente de validar con hardware.** (1) El cruce real: clonar el disco de un equipo licenciado a otro PC y comprobar que el código de equipo cambia y la licencia se rechaza. (2) El arranque en frío con la aplicación en el inicio de Windows: que el servicio de TPM responda dentro de los reintentos. (3) Un equipo sin TPM o con el TPM apagado: que caiga al nivel clásico con su aviso. (4) Un equipo con TPM presente pero sin aprovisionar por Windows (la clave de endoso aún no está en su identificador estándar): el código sale clásico y, cuando Windows aprovisiona, cambia al anclado; el registro de la aplicación distingue ese caso («el TPM no tiene la clave de endoso en su identificador estándar»). (5) Una máquina virtual con TPM virtual: un clon de la máquina conserva el chip virtual y comparte licencia.
