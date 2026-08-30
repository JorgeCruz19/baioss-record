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
LicenseFile=EULA.txt

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "Crear un acceso directo en el escritorio"; GroupDescription: "Accesos directos:"
Name: "startupicon"; Description: "Iniciar {#AppName} al encender el equipo"; GroupDescription: "Inicio automático:"

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

; Instrucciones para el cliente, EN la carpeta donde tiene que dejar los binarios.
Source: "FFMPEG-LEEME.txt"; DestDir: "{app}\tools\ffmpeg"; Flags: ignoreversion

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
Name: "{group}\Desinstalar {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon
; Inicio automático para TODOS los usuarios ({commonstartup}), no {userstartup}: como la
; instalación corre elevada, «userstartup» sería la carpeta de Inicio del ADMINISTRADOR que
; instala, no la del operador que luego usa el equipo — y el arranque automático no ocurriría.
Name: "{commonstartup}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: startupicon

[Run]
; Abrir la carpeta de FFmpeg va PRIMERO y marcado: sin esos dos binarios el programa no graba, así que es el
; paso que el cliente debe hacer antes que ninguna otra cosa.
Filename: "{app}\tools\ffmpeg"; Description: "Abrir la carpeta donde copiar FFmpeg (necesario para grabar)"; \
    Flags: shellexec nowait postinstall skipifsilent
Filename: "{app}\{#AppExeName}"; Description: "Abrir {#AppName} ahora"; Flags: nowait postinstall skipifsilent unchecked

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
    'Canales de grabación',
    'Cuántos canales quieres en este equipo',
    'Cada canal captura y graba una entrada de vídeo independiente. La aplicación mostrará exactamente los ' +
    'canales que elijas aquí; para cambiarlos más adelante basta con volver a ejecutar el instalador.',
    True, False);
  ChannelPage.Add('1 canal');
  for I := 2 to MaxChannels do
    ChannelPage.Add(IntToStr(I) + ' canales');
  { En una ACTUALIZACIÓN se preselecciona lo ya instalado; en una instalación nueva, el máximo. }
  ChannelPage.SelectedValueIndex := MaxChannels - 1;
  if RegQueryDWordValue(HKLM, 'Software\Baioss\RecordSetup', 'Channels', Existing) then
    if (Existing >= 1) and (Existing <= MaxChannels) then
      ChannelPage.SelectedValueIndex := Existing - 1;

  ModePage := CreateInputOptionPage(ChannelPage.ID,
    'Tipo de instalación',
    'Cómo quieres usar Baioss Record en este equipo',
    'Puedes empezar con el periodo de prueba y activar la licencia más adelante, sin reinstalar nada.',
    True, False);
  ModePage.Add('Periodo de prueba de 14 días');
  ModePage.Add('Ya tengo una licencia para este equipo');
  ModePage.SelectedValueIndex := ModeTrial;

  KeyPage := CreateInputQueryPage(ModePage.ID,
    'Licencia',
    'Introduce la licencia de este equipo',
    'Pega la licencia tal como te la enviaron. Se comprobará al abrir el programa; si no fuese válida, ' +
    'podrás volver a introducirla desde la ventana Licencia de la aplicación.');
  KeyPage.Add('Licencia:', False);
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
    MsgBox('Introduce la licencia, o vuelve atrás y elige el periodo de prueba.', mbError, MB_OK);
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
  Result := Result + 'Canales de grabación:' + NewLine + Space + SelectedChannelCount('') + NewLine + NewLine;
  if ModePage.SelectedValueIndex = ModeLicense then
    Result := Result + 'Tipo de instalación:' + NewLine + Space + 'Con licencia' + NewLine + NewLine
  else
    Result := Result + 'Tipo de instalación:' + NewLine + Space + 'Periodo de prueba de 14 días' + NewLine + NewLine;
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
  Msg := 'La instalación ha terminado.' + #13#10#13#10 +
         'FALTA UN PASO para poder grabar: copia «ffmpeg.exe» y «ffprobe.exe» en' + #13#10 +
         ExpandConstant('{app}\tools\ffmpeg') + #13#10 +
         'Encontrarás las instrucciones en el archivo FFMPEG-LEEME.txt de esa misma carpeta.';

  { En modo prueba se añade el código de equipo: es justo lo que el cliente necesita enviar para que le
    emitan su licencia. }
  if (ModePage.SelectedValueIndex = ModeTrial) and (MachineCode <> '') then
    Msg := Msg + #13#10#13#10 +
           'Tienes 14 días de prueba con todas las funciones. El código de este equipo es:' + #13#10 +
           MachineCode + #13#10 +
           'Envíaselo a tu proveedor para recibir la licencia permanente (también está en el botón Licencia).';

  WizardForm.FinishedLabel.Caption := Msg;
end;
