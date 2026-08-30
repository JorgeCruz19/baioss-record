# Componentes de terceros: qué obligaciones hay y dónde se cumplen

Resumen de lo que exige cada componente ajeno que viaja (o no) con Baioss Record, y en qué archivo del
producto se cumple. **Esto no es asesoría legal**: es la lectura de los términos publicados por cada
proveedor, con el trabajo mecánico ya hecho. La validación final corresponde a un abogado.

---

## NDI® (Vizrt NDI AB)

El producto **sí incluye** la librería de ejecución (`Processing.NDI.Lib.x64.dll`), lo cual **está
permitido**: el manual del SDK, §4.2, autoriza distribuir los binarios *«si los términos de tu EULA cubren
los requisitos específicos del EULA del SDK de NDI, y tu aplicación cumple los requisitos de la sección
License»*. Esas dos condiciones se traducen en lo siguiente.

| Obligación (fuente) | Dónde se cumple |
|---|---|
| Enlace a `https://ndi.video/` **cerca de donde NDI se usa o se selecciona** en el producto (SDK Doc., §License) | Ventana **🎛 Entradas**, pie: texto de marca + enlace pulsable |
| Enlace a `https://ndi.video/` en **la documentación** | `MANUAL-USUARIO.md` (§1.2) y este archivo |
| Mención *«NDI® es una marca registrada de Vizrt NDI AB»* en la **primera aparición** de la marca en cada documento y allí donde se den atribuciones | Ventana de Entradas, `AVISOS-TERCEROS.txt`, `EULA.txt` §7.2, `MANUAL-USUARIO.md` |
| Incluir `Processing.NDI.Lib.Licenses.txt` junto al binario (SDK Doc., §22 *3rd party rights*) | Lo copia el `.csproj` desde el NDI Runtime instalado, junto al `.exe` |
| No instalar las DLL de NDI en el *system path*, sino en la carpeta de la aplicación (SDK Doc., §License) | Van junto al ejecutable; el instalador no toca el PATH |
| No distribuir las **herramientas** NDI (solo enlazarlas) | No se distribuyen |
| Que el contrato de distribución prohíba modificar, hacer ingeniería inversa, eludir limitaciones técnicas y suprimir avisos; que incluya descargo de garantía y de responsabilidad a favor de NDI y sus licenciantes; y cumplimiento de control de exportaciones de EE. UU. (Licencia SDK, §3.d) | `EULA.txt` §7.2 |
| No sugerir patrocinio ni afiliación de NDI (Licencia SDK, §4.f) | Declarado expresamente en `AVISOS-TERCEROS.txt` y en el EULA |

### Lo que queda en tus manos (no puedo hacerlo yo)

1. **Aceptar la licencia del SDK** — ya lo hiciste al instalarlo y usarlo; conviene que conserves copia del
   PDF (`NDI SDK License Agreement.pdf`) con la versión del SDK que empleas.
2. **Validación del abogado** de la cláusula 7.2 del EULA (redactada, pendiente de revisión).
3. **Mantener la librería actualizada.** La licencia (§2.b) pide que el producto incorpore *«la última
   versión del SDK disponible»* en el momento de distribuir. Hoy se usa **NDI 6**. Conviene revisarlo en
   cada versión que publiques.
4. **Si algún día usas «NDI» dentro del nombre del producto**, hay que leer las *NDI Brand Guidelines*
   (vienen en el SDK) o consultar a su equipo. Hoy no aplica: el producto se llama Baioss Record.
5. **Opcional, recomendado:** Vizrt pide que les avises de aplicaciones comerciales que usen NDI
   (<https://ndi.video/resources/get-in-touch/>). No es un requisito para distribuir, pero es cortesía y te
   pone en su lista de productos compatibles.
6. **Advanced SDK:** si algún día se migrara al *NDI Advanced SDK* para uso comercial, hay que pedir un
   *vendor ID* a `licensing@ndi.video`. El SDK normal que se usa hoy **no** lo requiere.

---

## FFmpeg

**No se distribuye**: lo aporta el cliente. Ver `FFMPEG.md` para el motivo completo (el build habitual con
soporte DeckLink es *nonfree* y no es redistribuible) y cómo se le indica al cliente.

Al no distribuirlo, no heredamos sus obligaciones de distribución. Si algún día se empaquetara una
compilación GPL, habría que incluir su texto de licencia y ofrecer las fuentes correspondientes.

---

## Blackmagic Design (DeckLink)

No se distribuye ningún componente suyo: la captura se hace a través de FFmpeg (que el cliente instala) o
por DirectShow con los controladores que el propio cliente instala con su tarjeta. Solo se menciona la
marca para indicar compatibilidad, con la declaración correspondiente en `AVISOS-TERCEROS.txt`.

---

## Formatos de compresión (AAC, H.264, H.265)

El manual del SDK de NDI lo advierte expresamente, y aplica igual al resto del producto: el uso de estos
formatos **puede estar sujeto a licencias de patentes** (MPEG LA / Access Advance / Via LA según el caso).
Obtenerlas, si procede en tu mercado y volumen, corresponde a quien explota el producto. Está reflejado en
el EULA §7.3 y es un punto a comentar con el abogado.

---

## .NET y paquetes NuGet

Licencias permisivas (MIT / Apache 2.0). Basta con conservar sus avisos, recogidos en
`AVISOS-TERCEROS.txt`.
