# Checklist para vender Baioss Record

Lo que falta para poder cobrar por el producto, ordenado por lo que **impide vender** y lo que no.
Revisión: 2026-08-29.

**Ya está hecho:** licenciamiento offline (prueba de 14 días + licencia perpetua atada al equipo, con los
canales pagados firmados dentro de la clave), instalador por pasos con elección de canales, ofuscación de la
build de venta, manual de usuario y las auditorías de grabación 24/7 y de licenciamiento.

---

## 🔴 Bloqueantes

### 1. El FFmpeg que se empaqueta NO se puede redistribuir

**El problema.** El binario que viaja en `tools\ffmpeg\` lo dice en su propio archivo de licencia:

> `This version of ffmpeg has nonfree parts compiled in. Therefore it is not legally redistributable.`

Su línea de compilación incluye `--enable-gpl` **junto con** `--enable-nonfree --enable-libfdk-aac
--enable-decklink`. Esa combinación no se puede distribuir bajo ninguna licencia: bajo GPL no, porque
incorpora partes propietarias; bajo licencia propietaria tampoco, porque incorpora x264/x265 (GPL). Y el
instalador lo mete dentro (`publish\tools\ffmpeg\`), así que **cada copia vendida lo distribuye**.

**Por qué no es un descuido de quien compiló ese build.** El soporte DeckLink de FFmpeg (`-f decklink`)
EXIGE `--enable-nonfree`, porque el SDK de Blackmagic es propietario. Es decir: es nonfree precisamente por
la funcionalidad que da valor al producto. `libfdk-aac` (el códec de audio de mayor calidad) añade el mismo
problema por su cuenta.

**Opciones, de más rápida a más limpia:**

| Opción | Qué implica | Coste |
|---|---|---|
| **A. No empaquetarlo** | El cliente descarga su propio build. Usarlo él es legal; lo prohibido es que lo distribuyas TÚ. Hay que documentarlo en la instalación y avisar de que **sin FFmpeg el producto NO graba** (arranca en modo simulado). | Inmediato; empeora la experiencia de instalación |
| **B. Build GPL limpio + DeckLink por DirectShow** | Compilar FFmpeg con x264/x265 pero SIN `fdk-aac` ni `decklink`, y capturar la tarjeta por DirectShow (Blackmagic la expone como dispositivo; la app ya soporta dshow). Distribuible cumpliendo la GPL: incluir el texto de licencia y ofrecer las fuentes de FFmpeg. | Medio; hay que **evaluar** si dshow da el control de formato/latencia que necesitas |
| **C. Vía legal** | Licencia comercial de Fraunhofer para FDK-AAC y revisar con el abogado los términos del SDK de Blackmagic frente a la GPL de x264/x265. | Lento y con coste; es la única que conserva todo tal cual |

**Recomendación:** A para no bloquear la primera venta, evaluando B en paralelo. Decidir esto **antes** de la
primera factura, porque condiciona qué es capaz de hacer el producto que entregas.

### 2. El runtime de NDI también se redistribuye

`Processing.NDI.Lib.x64.dll` (~18 MB) viaja en el paquete. El propio `.csproj` ya lo advierte: redistribuirlo
exige aceptar y cumplir la licencia de NewTek/Vizrt (atribución de la marca NDI® y, según la versión del SDK,
registrar el producto). Hay que leer el EULA de la versión concreta del SDK que se usa y cumplirlo. Mucho
menos grave que lo de FFmpeg, pero es deuda legal igual.

**De paso:** el paquete no lleva **ningún** aviso de licencias de terceros. Conviene un `AVISOS-TERCEROS.txt`
junto al ejecutable (FFmpeg y sus dependencias, NDI, y los paquetes NuGet), que además es obligación expresa
de varias de esas licencias.

### 3. EULA revisado por un abogado, con los datos reales de la empresa

`installer\EULA.txt` es una **base de trabajo** y lo dice en una nota al final: faltan razón social, domicilio
y jurisdicción. Vender con texto legal de plantilla es riesgo puro.

### 4. Custodia de la clave privada y registro de licencias emitidas

Es el activo más frágil del negocio:

- **Si se pierde:** no puedes emitir ni una licencia más; habría que publicar una versión nueva con otra clave
  pública, y los clientes existentes quedarían sin poder reactivar.
- **Si se filtra:** cualquiera se fabrica licencias válidas.

Mínimo exigible: gestor de contraseñas **y** una copia fuera de línea en otra ubicación física.

Y llevar un **registro de licencias emitidas**, que no es burocracia: es la única defensa contra el clonado de
disco (ver *Límites conocidos* en `LICENCIAMIENTO.md`) y lo que permite reemitir cuando a un cliente se le
muere el equipo. Plantilla sugerida:

| Fecha | Cliente | Código de equipo | Canales | Nº de factura | Notas (reemisión, avería…) |
|---|---|---|---|---|---|

### 5. Firma digital del instalador

Sin firma, SmartScreen recibe al cliente con «editor desconocido» en la primera pantalla. Necesitas un
certificado de firma de código **OV** o **EV** (el EV además evita el periodo inicial de reputación). Se firma
tanto el `.exe` de la aplicación como el instalador; el comando está en `INSTALADOR.md`.

### 6. Lo vendible vive en ramas, no en `main`

A día de hoy `main` está en `7fe9ddb`: **sin** instalador, licenciamiento, canales ni ofuscación. Pendiente:

- `feat/instalador` → 9 commits por delante de `main` (instalador + licenciamiento + canales + ofuscación).
- `fix/decklink-dispositivo-persistente` → 2 commits (no fusionar hasta validar con la tarjeta real).

---

## 🟡 Validación pendiente antes de entregar

- **DeckLink con la tarjeta real.** La rama del dispositivo persistente nunca se ha probado con hardware.
  Incluye una grabación **larga (>2 h)** por el asunto de los timestamps del transporte MPEG-TS, y una prueba
  de tirón de cable SDI a mitad de grabación.
- **Instalación en una máquina o VM limpia, con la build OFUSCADA.** Es la combinación que nunca se ha probado
  entera: clave HKLM de canales, código de equipo en la pantalla final, licencia pendiente aplicada al primer
  arranque, y reinstalar encima respetando base de datos, grabaciones y licencia.
- **Soak 24/7** de varios días con la build final en la máquina de producción.

---

## 🟢 Deuda conocida, no bloqueante

- **La API local no tiene autenticación** (hallazgo A10 de la auditoría 24/7). Escucha solo en loopback
  (`127.0.0.1:5005`), así que en un equipo dedicado el riesgo es bajo; conviene resolverlo si algún día se
  expone a la red.
- Pendientes menores de las auditorías: sonda de recuperación NDI (#39/#59), desfase A/V de NDI bajo carga
  (A3/#35) y configuración de FFmpeg (#47/#48).
- **Parcheo del binario:** fuera del modelo de amenaza; la build de venta va ofuscada, que sube el coste pero
  no lo elimina. Ver `LICENCIAMIENTO.md` §5 y §6.

---

## Orden recomendado

1. Decidir la vía de **FFmpeg** (condiciona el producto entero).
2. **Abogado**: EULA + datos de empresa + revisión de los términos de terceros (FFmpeg/NDI/Blackmagic).
3. **Clave privada** a buen recaudo y registro de licencias abierto.
4. Fusionar `feat/instalador` a `main` y **validar en VM limpia**.
5. **Certificado** de firma de código y firmar la build.
6. Validación DeckLink con tarjeta real → fusionar esa rama si pasa.
7. Soak 24/7 de la build final.
