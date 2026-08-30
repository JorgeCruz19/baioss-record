# FFmpeg: por qué no se distribuye y cómo lo instala el cliente

## La decisión

**Baioss Record NO empaqueta FFmpeg.** El instalador crea la carpeta `tools\ffmpeg\` con un archivo
`FFMPEG-LEEME.txt`, y es el **cliente** quien descarga los binarios y los deja ahí.

**El motivo es legal, no técnico.** El build de FFmpeg que se usa en desarrollo está compilado con
`--enable-gpl` **junto a** `--enable-nonfree --enable-libfdk-aac --enable-decklink`, y su propio archivo de
licencia lo dice sin rodeos:

> `This version of ffmpeg has nonfree parts compiled in. Therefore it is not legally redistributable.`

Esa combinación no se puede distribuir bajo ninguna licencia: bajo GPL no, porque incorpora partes
propietarias; bajo licencia propietaria tampoco, porque incorpora x264/x265 (GPL). Y no es un descuido de quien
lo compiló: **el soporte DeckLink de FFmpeg exige `--enable-nonfree`**, porque el SDK de Blackmagic es
propietario. Es decir, es nonfree justo por la funcionalidad que da valor al producto.

Lo prohibido es que **nosotros** lo distribuyamos. Que el cliente descargue y use ese mismo build en su equipo
es perfectamente legal, y es lo que hace todo el mundo que usa FFmpeg con tarjetas Blackmagic.

## Qué ve el cliente

1. **Al terminar la instalación**, la última pantalla del asistente le dice que falta ese paso, con la ruta
   exacta, y una casilla marcada abre esa carpeta en el Explorador.
2. **En la carpeta** encuentra `FFMPEG-LEEME.txt`: qué descargar, dónde dejarlo y cómo comprobar que la
   compilación sirve (incluido el comando que lista las tarjetas DeckLink).
3. **Si abre el programa sin haberlo hecho**, aparece un aviso claro —con la ruta— explicando que arranca en
   modo de demostración y que no grabará hasta instalarlo. El aviso se repite en cada arranque mientras falte;
   es deliberado: un grabador que no graba en silencio es mucho peor que uno que insiste.

## Qué cambia para el desarrollo

**Nada.** `tools\ffmpeg\` sigue igual en el repositorio y `scripts\publish.ps1` sigue copiando los binarios a
`publish\` para poder probar en local. La exclusión ocurre **solo al construir el instalador**
(`Excludes: tools\ffmpeg\*` en `installer\baioss-record.iss`), así que el paquete que se entrega al cliente no
los lleva y la carpeta de trabajo del desarrollador sí.

## Alternativa si algún día se quiere volver a empaquetar

Habría que compilar FFmpeg **sin** `fdk-aac` ni `decklink` (build GPL «limpio», sí redistribuible cumpliendo la
GPL: incluir el texto de licencia y ofrecer las fuentes) y capturar las tarjetas Blackmagic por **DirectShow**,
que la app ya soporta y que Blackmagic expone como dispositivo. Queda pendiente evaluar si por esa vía se
conserva el control de formato y la latencia que da `-f decklink`. Ver `CHECKLIST-VENTA.md`.
