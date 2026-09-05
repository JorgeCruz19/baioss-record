; ============================================================================
;  Instalador de Baioss Record (Inno Setup 6)
;
;  Se compila con:  scripts\build-installer.ps1
;  (ese script publica primero la app en .\publish\ y luego invoca a ISCC)
;
;  DECISIONES QUE CONVIENE CONOCER:
;
;  * NO se instala en «Archivos de programa». La aplicación escribe data\, logs\ y
;    recordings\ JUNTO a su ejecutable y corre sin privilegios de administrador; dentro
;    de Archivos de programa Windows se lo impediría y no podría ni grabar ni guardar su
;    base de datos. Por eso el destino por defecto es C:\Baioss\Record y, además, se
;    concede permiso de escritura al grupo Usuarios sobre esa carpeta.
;
;  * Al desinstalar NO se borran las grabaciones ni la base de datos: son material del
;    cliente. Solo se retira el programa.
;
;  * EL ASISTENTE HABLA ESPAÑOL E INGLÉS. Ningún texto propio va escrito «a pelo» en las
;    secciones: todos salen de [CustomMessages] y se leen con {cm:Clave} o, en el [Code],
;    con CustomMessage('Clave'). Si añades un texto, añádelo en LOS DOS idiomas — si falta
;    en uno, Inno no compila, que es justo lo que queremos.
;
;  * ESTE ARCHIVO SE GUARDA EN UTF-8 CON BOM. Sin el BOM, Inno lo interpreta con la página
;    de códigos ANSI del sistema y los acentos de los textos del asistente salen rotos
;    («instalaciÃ³n»). Si tu editor te lo quita al guardar, vuelve a ponerlo.
; ============================================================================

#define AppName        "Baioss Record"
; Valor por DEFECTO: el script de construcción lo pasa con /DAppVersion=x.y.z. Sin el ifndef, este #define
; PISABA el de la línea de comandos y el parámetro -Version del script no tenía ningún efecto.
#ifndef AppVersion
#define AppVersion     "1.0.0"
#endif
#define AppPublisher   "Baioss"
#define AppExeName     "Baioss.Record.App.exe"
#define SourceDir      "..\publish"

[Setup]
AppId={{7C4B1E92-3D5A-4F18-9B2C-8A6E0D7F1C34}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={sd}\Baioss\Record
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=BaiossRecord-{#AppVersion}-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
; Se requiere administrador para instalar fuera del perfil del usuario y crear los
; accesos directos para todos los usuarios del equipo.
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#AppExeName}
SetupIconFile=..\assets\icon.ico
; El contrato NO se declara aquí sino en [Languages]: cada idioma muestra su propio EULA.
; «auto» = solo se pregunta el idioma si Windows no es ninguno de los dos; con un Windows en
; español o en inglés el asistente arranca directamente en ese idioma, igual que hace la
; aplicación. Es deliberado no dar la lata con una pantalla más cuando la respuesta se sabe.
ShowLanguageDialog=auto

[Languages]
; El PRIMERO es el que se usa si Windows no está en español (es decir, el de reserva), del mismo
; modo que la aplicación: cualquier variante de español → español; cualquier otro idioma → inglés.
Name: "en"; MessagesFile: "compiler:Default.isl"; LicenseFile: "EULA-EN.txt"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"; LicenseFile: "EULA.txt"

[CustomMessages]
; ---- Tareas y accesos directos ----
en.Task_ShortcutsGroup=Shortcuts:
es.Task_ShortcutsGroup=Accesos directos:
en.Task_DesktopIcon=Create a desktop shortcut
es.Task_DesktopIcon=Crear un acceso directo en el escritorio
en.Task_StartupGroup=Automatic startup:
es.Task_StartupGroup=Inicio automático:
en.Task_Startup=Start %1 when the computer starts
es.Task_Startup=Iniciar %1 al encender el equipo

; ---- Acciones al terminar ----
en.Run_OpenFfmpegFolder=Open the folder where FFmpeg has to be copied (required in order to record)
es.Run_OpenFfmpegFolder=Abrir la carpeta donde copiar FFmpeg (necesario para grabar)

; ---- Página: número de canales ----
en.Ch_Title=Recording channels
es.Ch_Title=Canales de grabación
en.Ch_Subtitle=How many channels this computer will have
es.Ch_Subtitle=Cuántos canales quieres en este equipo
en.Ch_Desc=Each channel captures and records one independent video input. The application will show exactly the channels you choose here; to change them later, simply run this installer again.
es.Ch_Desc=Cada canal captura y graba una entrada de vídeo independiente. La aplicación mostrará exactamente los canales que elijas aquí; para cambiarlos más adelante basta con volver a ejecutar el instalador.
en.Ch_One=1 channel
es.Ch_One=1 canal
en.Ch_Many=%1 channels
es.Ch_Many=%1 canales

; ---- Página: tipo de instalación ----
en.Mode_Title=Installation type
es.Mode_Title=Tipo de instalación
en.Mode_Subtitle=How you want to use %1 on this computer
es.Mode_Subtitle=Cómo quieres usar %1 en este equipo
en.Mode_Desc=You can start with the trial period and activate your license later on, without reinstalling anything.
es.Mode_Desc=Puedes empezar con el periodo de prueba y activar la licencia más adelante, sin reinstalar nada.
en.Mode_Trial=14-day trial period
es.Mode_Trial=Periodo de prueba de 14 días
en.Mode_License=I already have a license for this computer
es.Mode_License=Ya tengo una licencia para este equipo

; ---- Página: licencia ----
en.Key_Title=License
es.Key_Title=Licencia
en.Key_Subtitle=Enter the license for this computer
es.Key_Subtitle=Introduce la licencia de este equipo
en.Key_Desc=Paste the license exactly as you received it. It will be checked when you open the program; if it turned out not to be valid, you will be able to enter it again from the application's License window.
es.Key_Desc=Pega la licencia tal como te la enviaron. Se comprobará al abrir el programa; si no fuese válida, podrás volver a introducirla desde la ventana Licencia de la aplicación.
en.Key_Label=License:
es.Key_Label=Licencia:
en.Key_Empty=Enter the license, or go back and choose the trial period.
es.Key_Empty=Introduce la licencia, o vuelve atrás y elige el periodo de prueba.

; ---- Resumen antes de instalar ----
en.Memo_Channels=Recording channels:
es.Memo_Channels=Canales de grabación:
en.Memo_Mode=Installation type:
es.Memo_Mode=Tipo de instalación:
en.Memo_Licensed=Licensed
es.Memo_Licensed=Con licencia

; ---- Última página del asistente ----
en.Fin_Done=Installation is complete.
es.Fin_Done=La instalación ha terminado.
; %1 = carpeta tools\ffmpeg del destino. Ojo: cada idioma nombra SU archivo de instrucciones
; (el instalador deja los dos en esa carpeta).
en.Fin_Ffmpeg=ONE STEP IS STILL MISSING before you can record: copy ffmpeg.exe and ffprobe.exe into%n%1%nYou will find the instructions in the FFMPEG-README.txt file in that same folder.
es.Fin_Ffmpeg=FALTA UN PASO para poder grabar: copia «ffmpeg.exe» y «ffprobe.exe» en%n%1%nEncontrarás las instrucciones en el archivo FFMPEG-LEEME.txt de esa misma carpeta.
; %1 = código de equipo.
en.Fin_Trial=You have a 14-day trial with every feature. The code for this computer is:%n%1%nSend it to your supplier to receive your permanent license (it is also shown behind the License button).
es.Fin_Trial=Tienes 14 días de prueba con todas las funciones. El código de este equipo es:%n%1%nEnvíaselo a tu proveedor para recibir la licencia permanente (también está en el botón Licencia).

[Tasks]
Name: "desktopicon"; Description: "{cm:Task_DesktopIcon}"; GroupDescription: "{cm:Task_ShortcutsGroup}"
Name: "startupicon"; Description: "{cm:Task_Startup,{#AppName}}"; GroupDescription: "{cm:Task_StartupGroup}"

[Files]
; Se EXCLUYE lo que genera la aplicación al ejecutarse. La carpeta publish\ es también la
; que se usa para probar en desarrollo, así que sin estos «Excludes» el instalador se
; llevaría la base de datos de pruebas, los registros y las grabaciones del desarrollador
; hasta el equipo del cliente (que además arrancaría con canales y sesiones ajenos).
; «_obfuscated\*» y «Mapping.txt» son subproductos de la ofuscación (scripts\obfuscate.ps1): la carpeta
; intermedia con los DLL ya copiados a su sitio, y el mapa nombre-original→ofuscado, que NUNCA debe distribuirse.
;
; «tools\ffmpeg\*» se EXCLUYE a propósito: los binarios de FFmpeg que se usan en desarrollo son un build
; «nonfree» (lleva --enable-nonfree por el soporte DeckLink, y libfdk-aac) y su propia licencia dice que NO es
; legalmente redistribuible. Lo descarga e instala el CLIENTE en su equipo —usarlo él es legal; distribuirlo
; nosotros no—. La carpeta se crea vacía con un LÉEME que explica exactamente qué bajar y dónde ponerlo.
; (Ver docs\CHECKLIST-VENTA.md y docs\FFMPEG.md.)
Source: "{#SourceDir}\*"; DestDir: "{app}"; \
    Excludes: "data\*,logs\*,recordings\*,_obfuscated\*,Mapping.txt,tools\ffmpeg\*,*.log,*.db,*.db-shm,*.db-wal"; \
    Flags: ignoreversion recursesubdirs createallsubdirs

; Instrucciones para el cliente, EN la carpeta donde tiene que dejar los binarios. Se copian LOS DOS
; idiomas independientemente del idioma del asistente: quien instala (informática) y quien luego opera el
; equipo no tienen por qué ser la misma persona, y la aplicación —cuyo idioma se cambia en caliente— nombra
; el archivo de SU idioma en el aviso de FFmpeg que falta.
Source: "FFMPEG-LEEME.txt"; DestDir: "{app}\tools\ffmpeg"; Flags: ignoreversion
Source: "FFMPEG-README.txt"; DestDir: "{app}\tools\ffmpeg"; Flags: ignoreversion

; Atribuciones de terceros (NDI®, FFmpeg, Blackmagic…). La licencia del SDK de NDI exige la mención de marca
; registrada y el enlace a ndi.video; varias licencias de los paquetes usados exigen conservar sus avisos.
; También en los dos idiomas, por el mismo motivo y porque es material legal: conviene que esté disponible
; en el idioma del cliente sea cual sea el que eligió quien instaló.
Source: "AVISOS-TERCEROS.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

; El contrato aceptado durante la instalación, para poder consultarlo después. Los dos idiomas, por lo mismo.
Source: "EULA.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "EULA-EN.txt"; DestDir: "{app}"; Flags: ignoreversion

[Dirs]
; Permiso de ESCRITURA para los usuarios: la app guarda aquí su base de datos, los
; registros y (si no se cambia) las grabaciones. Sin esto no arrancaría bien.
Name: "{app}"; Permissions: users-modify
Name: "{commonappdata}\Baioss\Record"; Permissions: users-modify
; El cliente deja aquí ffmpeg.exe y ffprobe.exe (ver FFMPEG-LEEME.txt): necesita poder ESCRIBIR en la carpeta
; sin ser administrador, o no podrá copiarlos.
Name: "{app}\tools\ffmpeg"; Permissions: users-modify

[Registry]
; TERCERA copia del estado de licencia/prueba, COMPARTIDA por todos los usuarios del equipo. Las otras dos
; (archivo en ProgramData y HKCU) las puede borrar un usuario estándar («borro el archivo y entro con otra
; cuenta» reiniciaba la prueba); esta clave la crea el instalador ELEVADO y le concede escritura a Usuarios
; para que la app (que corre sin elevación) pueda mantenerla. NO se borra al desinstalar a propósito:
; desinstalar y reinstalar no debe reiniciar el periodo de prueba.
Root: HKLM; Subkey: "Software\Baioss\Record"; Permissions: users-modify; Flags: noerror
; Nº de CANALES elegido en el asistente (1-4). Clave HERMANA de la anterior a propósito: aquella lleva
; users-modify (y los permisos se HEREDAN a las subclaves), mientras que esta conserva la ACL por defecto de
; HKLM — los usuarios la leen, pero solo un administrador la cambia ⇒ cambiar de canales = reinstalar, no
; editar un archivo. La app la lee al arrancar y muestra exactamente esos canales.
Root: HKLM; Subkey: "Software\Baioss\RecordSetup"; ValueType: dword; ValueName: "Channels"; ValueData: "{code:SelectedChannelCount}"; Flags: noerror

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
; «UninstallProgram» es un mensaje que ya trae traducido cada archivo de idioma de Inno.
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon
; Inicio automático para TODOS los usuarios ({commonstartup}), no {userstartup}: como la
; instalación corre elevada, «userstartup» sería la carpeta de Inicio del ADMINISTRADOR que
; instala, no la del operador que luego usa el equipo — y el arranque automático no ocurriría.
Name: "{commonstartup}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: startupicon

[Run]
; Abrir la carpeta de FFmpeg va PRIMERO y marcado: sin esos dos binarios el programa no graba, así que es el
; paso que el cliente debe hacer antes que ninguna otra cosa.
Filename: "{app}\tools\ffmpeg"; Description: "{cm:Run_OpenFfmpegFolder}"; \
    Flags: shellexec nowait postinstall skipifsilent
; «LaunchProgram» también viene traducido de serie con Inno.
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent unchecked

[UninstallDelete]
; Solo lo que genera el programa al ejecutarse; NUNCA las grabaciones ni la base de datos.
Type: filesandordirs; Name: "{app}\logs"

[Code]
var
  ChannelPage: TInputOptionWizardPage;
  ModePage: TInputOptionWizardPage;
  KeyPage: TInputQueryWizardPage;
  MachineCode: String;

const
  ModeTrial = 0;
  ModeLicense = 1;
  MaxChannels = 4;

{ Nº de canales elegido (1-4), como texto para el ValueData dword de [Registry]. }
function SelectedChannelCount(Param: String): String;
begin
  Result := IntToStr(ChannelPage.SelectedValueIndex + 1);
end;

procedure InitializeWizard;
var
  I, Existing: Cardinal;
begin
  ChannelPage := CreateInputOptionPage(wpSelectTasks,
    CustomMessage('Ch_Title'),
    CustomMessage('Ch_Subtitle'),
    CustomMessage('Ch_Desc'),
    True, False);
  ChannelPage.Add(CustomMessage('Ch_One'));
  for I := 2 to MaxChannels do
    ChannelPage.Add(FmtMessage(CustomMessage('Ch_Many'), [IntToStr(I)]));
  { En una ACTUALIZACIÓN se preselecciona lo ya instalado; en una instalación nueva, el máximo. }
  ChannelPage.SelectedValueIndex := MaxChannels - 1;
  if RegQueryDWordValue(HKLM, 'Software\Baioss\RecordSetup', 'Channels', Existing) then
    if (Existing >= 1) and (Existing <= MaxChannels) then
      ChannelPage.SelectedValueIndex := Existing - 1;

  ModePage := CreateInputOptionPage(ChannelPage.ID,
    CustomMessage('Mode_Title'),
    FmtMessage(CustomMessage('Mode_Subtitle'), ['{#AppName}']),
    CustomMessage('Mode_Desc'),
    True, False);
  ModePage.Add(CustomMessage('Mode_Trial'));
  ModePage.Add(CustomMessage('Mode_License'));
  ModePage.SelectedValueIndex := ModeTrial;

  KeyPage := CreateInputQueryPage(ModePage.ID,
    CustomMessage('Key_Title'),
    CustomMessage('Key_Subtitle'),
    CustomMessage('Key_Desc'));
  KeyPage.Add(CustomMessage('Key_Label'), False);
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  { La página de la clave solo se muestra si el usuario eligió «ya tengo una licencia». }
  Result := (PageID = KeyPage.ID) and (ModePage.SelectedValueIndex <> ModeLicense);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  { No se deja continuar con el campo vacío: es un despiste muy fácil de cometer. }
  Result := True;
  if (CurPageID = KeyPage.ID) and (Trim(KeyPage.Values[0]) = '') then
  begin
    MsgBox(CustomMessage('Key_Empty'), mbError, MB_OK);
    Result := False;
  end;
end;

procedure ReadMachineCode;
var
  TempFile: String;
  ResultCode: Integer;
  Lines: TArrayOfString;
begin
  { Consulta el código de este equipo ejecutando la app ya instalada con «--machine-code».
    Se hace así, y no replicando el cálculo aquí, para que sea EXACTAMENTE el mismo que usa
    el programa: si difiriera en un solo bit, las licencias emitidas no validarían. }
  MachineCode := '';
  TempFile := ExpandConstant('{tmp}\machine-code.txt');
  if Exec(ExpandConstant('{app}\{#AppExeName}'), '--machine-code "' + TempFile + '"',
          '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if LoadStringsFromFile(TempFile, Lines) and (GetArrayLength(Lines) > 0) then
      MachineCode := Trim(Lines[0]);
  end;
end;

procedure WritePendingLicense;
var
  Dir: String;
begin
  { Deja la licencia preparada para que la aplique la app en su primer arranque. El instalador
    no puede escribir directamente el estado de licencia porque va firmado con una clave
    derivada de la huella del equipo: se deja la clave en claro y la valida el programa. }
  Dir := ExpandConstant('{commonappdata}\Baioss\Record');
  ForceDirectories(Dir);
  SaveStringToFile(Dir + '\pending-license.txt', Trim(KeyPage.Values[0]), False);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if ModePage.SelectedValueIndex = ModeLicense then
      WritePendingLicense
    else
      ReadMachineCode;
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo + NewLine + NewLine;
  Result := Result + CustomMessage('Memo_Channels') + NewLine + Space + SelectedChannelCount('') + NewLine + NewLine;
  Result := Result + CustomMessage('Memo_Mode') + NewLine + Space;
  if ModePage.SelectedValueIndex = ModeLicense then
    Result := Result + CustomMessage('Memo_Licensed') + NewLine + NewLine
  else
    Result := Result + CustomMessage('Mode_Trial') + NewLine + NewLine;
  if MemoTasksInfo <> '' then
    Result := Result + MemoTasksInfo + NewLine;
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Msg: String;
begin
  if CurPageID <> wpFinished then Exit;

  { PASO OBLIGATORIO primero: sin FFmpeg el programa abre pero NO graba. Se explica aquí, se deja un LÉEME en
    la propia carpeta y además la casilla de la página anterior la abre en el Explorador. }
  Msg := CustomMessage('Fin_Done') + #13#10#13#10 +
         FmtMessage(CustomMessage('Fin_Ffmpeg'), [ExpandConstant('{app}\tools\ffmpeg')]);

  { En modo prueba se añade el código de equipo: es justo lo que el cliente necesita enviar para que le
    emitan su licencia. }
  if (ModePage.SelectedValueIndex = ModeTrial) and (MachineCode <> '') then
    Msg := Msg + #13#10#13#10 + FmtMessage(CustomMessage('Fin_Trial'), [MachineCode]);

  WizardForm.FinishedLabel.Caption := Msg;
  { IMPRESCINDIBLE. Inno dimensiona esta etiqueta para SU texto de despedida (tres líneas, 61 px de
    alto) ANTES de llamar aquí; si nos limitamos a cambiar el Caption, todo lo que sobrepase ese alto
    se recorta sin más aviso — y lo que se perdía era justamente la ruta donde copiar FFmpeg. Medido:
    61 px antes, 106 px después. }
  WizardForm.FinishedLabel.AdjustHeight;
end;
