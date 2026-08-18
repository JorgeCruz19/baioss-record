# Licenciamiento — Baioss Record

Periodo de prueba de **14 días** y licencia **perpetua atada a un equipo**. Todo funciona **sin conexión**: no hay servidor de licencias ni activación por internet.

---

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
2. Emites la licencia:

```bash
dotnet run --project tools/Baioss.Record.LicenseTool -- issue --machine D68J-FZNH-E2JV-XY2E --private <TU-CLAVE-PRIVADA>
```

3. Le envías la clave resultante. Él la pega en la misma ventana y pulsa **Activar**.

La licencia **solo funciona en ese equipo**: la huella del PC forma parte de lo que se firma, así que en otro ordenador la firma no valida.

> La herramienta es estricta con el código de equipo: si le faltan o sobran caracteres **se niega a emitir** (antes firmaba en silencio una licencia que jamás validaría en el equipo del cliente). También se niega si el código corresponde a un equipo **sin identificadores utilizables** — esa huella es genérica y una licencia emitida para ella funcionaría en cualquier equipo en ese estado.

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

**La huella se compone de** MachineGuid de Windows, número de serie del volumen del sistema y los seriales de placa/equipo de SMBIOS. Se leen del **registro y de una API nativa, nunca por WMI**: una consulta WMI puede tardar segundos o fallar al arrancar, y de esto depende poder grabar.

**El código de equipo y la licencia se leen en voz alta.** Usan un alfabeto sin caracteres confundibles (nada de `I`, `L`, `O`, `U`) y al teclear se aceptan minúsculas, espacios y las confusiones típicas (`O`→`0`, `I`→`1`).

**El periodo de prueba resiste juegos con el reloj.** Se guarda la fecha de inicio, una marca de agua (el máximo momento visto, que nunca baja) y el **uso real acumulado medido con reloj monotónico**. Atrasar la hora no alarga la prueba; adelantarla y devolverla tampoco la congela. Un ajuste horario legítimo (NTP, cambio de zona) no resta días. Y un reloj **accidentalmente puesto en el futuro** (pila CMOS, año mal tecleado) tampoco quema la prueba: la marca de agua no puede avanzar más deprisa que el tiempo real medido, así que solo se consume lo que de verdad pasó.

**El estado vive en `%ProgramData%\Baioss\Record\license.dat`**, con copia en el registro del **equipo** (HKLM, la crea el instalador y la comparte todo usuario del PC) y en el del **usuario** (HKCU). Fuera de la carpeta del programa a propósito: actualizar el producto suele reemplazarla y se llevaría por delante la marca de la prueba. Al leer se toma **la fecha más antigua** encontrada, así que borrar una copia no reinicia nada — y la copia HKLM cubre además el «borro el archivo y entro con otra cuenta de Windows». Todas las copias van firmadas con una clave derivada de la huella: una marca copiada de otro PC no cuela.

**Si el estado guardado no valida, la clave se conserva.** Un fallo transitorio al leer un identificador del equipo puede cambiar la huella una sesión (y con ella la firma del estado). En ese caso se reinician las marcas de la prueba, pero la **clave de licencia se rescata** y se re-verifica: el cliente legítimo no pierde su activación por un hipo de E/S. Y si el estado directamente **no se puede leer** (archivo bloqueado por el antivirus o la copia de seguridad), no se toca nada: se queda en «no verificable» y se reintenta al minuto.

**Activar solo confirma si de verdad quedó guardado.** Si la clave es válida pero no se pudo escribir en ninguna ubicación (permisos rotos), el botón lo dice claramente en vez de fingir éxito — y la licencia dejada por el instalador se conserva para reintentar en el próximo arranque.

**Nunca se guarda un «ya activado».** Del disco solo sale el *texto* de la clave; que la licencia sea válida se decide **verificando la firma en cada arranque**. Si se guardara una bandera, bastaría editarla.

**A prueba de fallos propios.** Si no se puede leer el estado (permisos, archivo corrupto, error interno), el estado es «no verificable» y **no se bloquea nada**: se avisa por registro y se sigue grabando.

---

## 5. Límite conocido

Clonar el disco de un equipo licenciado (imagen sin *Sysprep*) reproduce también su huella, así que la licencia valdría en el clon. **Ninguna licencia sin conexión puede evitarlo.** Mitigación práctica: lleva un registro de a qué código de equipo has emitido cada licencia; si un mismo código pide varias, es señal de aviso.

---

## 6. Comprobar el estado sin abrir la interfaz

```bash
curl http://127.0.0.1:5005/api/v1/license
```

Devuelve el estado, los días restantes y el código de equipo. También existe `POST /api/v1/license/activate` con `{"key":"..."}` (responde **422** si la clave no es válida, nunca un error de servidor).
