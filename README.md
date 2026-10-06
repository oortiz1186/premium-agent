# CRM MIDA Premium Agent

Puente Windows x86 entre CRM MIDA y el SDK de CONTPAQi Comercial Premium.

## Requisitos

- Windows con CONTPAQi Comercial Premium instalado.
- SDK de Comercial disponible en `C:\Program Files (x86)\Compac\COMERCIAL`.
- `MGWServicios.dll` y `CAC.ini` de la misma versión de Premium.
- Empresa de pruebas disponible, inicialmente `C:\Compac\Empresas\adEMPRESA_PRUEBA`.

## Publicar

```powershell
dotnet publish -c Release -r win-x86 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish
```

Copiar el contenido de `publish` al servidor, por ejemplo a:

```text
C:\MIDA\PremiumAgent
```

## Configuración inicial

Editar `appsettings.json` y cambiar como mínimo `PremiumAgent:ApiKey`.

El paquete se publica listo para la empresa de pruebas del servidor. Antes de habilitar escrituras, validar primero `/health`. Conservar inicialmente:

```json
"CompanyDirectory": "C:\\Compac\\Empresas\\adEMPRESA_PRUEBA",
"AllowedCompanyDirectoryName": "adEMPRESA_PRUEBA",
"AllowWrites": false
```

El agente bloquea escrituras si el nombre de la empresa real no coincide con `AllowedCompanyDirectoryName`.

## Ejecutar manualmente

```powershell
cd C:\MIDA\PremiumAgent
.\CrmMida.PremiumAgent.exe
```

Validar desde el mismo servidor:

```powershell
Invoke-RestMethod http://localhost:5095/health
```

## Probar API autenticada

```powershell
$headers = @{ "X-CRM-MIDA-Key" = "TU_CLAVE" }
Invoke-RestMethod http://localhost:5095/api/customers/by-code/CRMTEST001 -Headers $headers
```

Alta de prueba:

```powershell
$headers = @{ "X-CRM-MIDA-Key" = "TU_CLAVE" }
$body = @{
  code = "CRMTEST002"
  businessName = "CLIENTE PRUEBA AGENTE CRM MIDA"
  tradeName = "PRUEBA AGENTE"
  rfc = "XAXX010101000"
  email = ""
  phone = ""
  contactName = ""
} | ConvertTo-Json
Invoke-RestMethod http://localhost:5095/api/customers -Method Post -Headers $headers -ContentType "application/json" -Body $body
```

> Si ya existe el RFC usado en Premium, el agente devuelve conflicto y no crea un duplicado. Para una segunda prueba de alta se debe usar un RFC de pruebas distinto/permitido.

## Conectar CRM MIDA

En la configuración del backend CRM:

```env
CommercialPremium__WriterBaseUrl=http://IP_SERVIDOR_PREMIUM:5095
CommercialPremium__WriterApiKey=LA_MISMA_CLAVE_DEL_AGENTE
```

El endpoint CRM `/api/v1/contpaqi/customers/resolve-or-create` ya utiliza ese agente cuando el RFC no existe ni en CRM ni en Comercial Premium.

## Instalar como servicio Windows

Primero validar el agente manualmente. Después, en PowerShell como administrador:

```powershell
sc.exe create "CrmMidaPremiumAgent" binPath= "C:\MIDA\PremiumAgent\CrmMida.PremiumAgent.exe" start= auto DisplayName= "CRM MIDA Premium Agent"
sc.exe start "CrmMidaPremiumAgent"
```

Para consultar estado:

```powershell
Get-Service CrmMidaPremiumAgent
```

## Firewall

No publicar el puerto 5095 a Internet. Permitirlo únicamente desde el servidor CRM o desde la red privada que comunica ambos servidores.
