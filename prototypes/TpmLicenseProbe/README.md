# Prototipo: licencia atada al TPM

Mide si la licencia de Baioss Record puede atarse al TPM del equipo (para cerrar el clonado de disco) y a qué coste.
NO forma parte de la solución ni del instalador. Resultados y diseño propuesto: `docs/LICENCIAMIENTO.md`, sección
«Prototipo: licencia atada al TPM».

```bash
dotnet build -c Release
./bin/Release/net8.0-windows/TpmLicenseProbe.exe            # todo, sin elevación (borra sus claves al terminar; --keep las conserva)
./bin/Release/net8.0-windows/TpmLicenseProbe.exe --machine-create   # EJECUTAR COMO ADMINISTRADOR: crea dos claves de máquina (escribe machine-create.log)
./bin/Release/net8.0-windows/TpmLicenseProbe.exe --machine-use      # sin elevación: ¿puede abrirlas y firmar?
./bin/Release/net8.0-windows/TpmLicenseProbe.exe --cleanup          # borra las claves del prototipo (las de máquina solo elevado)
./bin/Release/net8.0-windows/TpmLicenseProbe.exe --tbs              # lee la clave de endoso DIRECTAMENTE del chip (TBS) y la compara con la que da Windows
```

Qué prueba: (A) leer la clave de endoso del chip y el UUID del SMBIOS sin crear claves; (B) una clave ECDSA P-256 no
exportable de usuario: crear, abrir, firmar retos, verificar, que la privada no salga, y que otro proceso la use; (C) una
clave de máquina: permisos para crearla y para usarla sin elevación; y el flujo de licencia completo con rechazo de
licencias ajenas o manipuladas.
