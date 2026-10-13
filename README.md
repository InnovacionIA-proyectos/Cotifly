# Cotifly — Bot cotizador de Botero Soto

Backend de Cotifly: un comercial captura la solicitud de cotización de flete en la consola web de ClientePro, Nexo la
costea, el backend resuelve margen y precio, arma el PDF y lo envía. El backend vigente es **.NET 10**
(`backend-dotnet/`). El bot anterior en Python (orquestador, servidor MCP y Google Chat) se retiró del repositorio en
HU-022.1 (#146); su último estado vive en el tag `python-final`.

> Corrección mínima de HU-022.1. La reescritura completa de este índice queda para la parte 4 de HU-022.1.

## Estructura

```
backend-dotnet/   Backend .NET 10 (Clean Architecture): código, pruebas, specs y documentación
db/               Esquema dbo_cotifly (SQL Server 2019): scripts, migraciones y seeders
docs/             Documentación funcional: base de datos, conexión con Nexo, frontend y backlog
infra/            Manifiestos de Swarm/Dokploy del backend .NET
scripts/          Utilidades del repositorio (verificador de enlaces de los .md)
```

## Dónde seguir

- Backend .NET (ejecución local, pruebas, arquitectura): [`backend-dotnet/README.md`](backend-dotnet/README.md).
- Documentación funcional: [`docs/README.md`](docs/README.md).
- Base de datos: [`BASE_DE_DATOS.md`](BASE_DE_DATOS.md) y [`db/README.md`](db/README.md).
- Despliegue: [`DEPLOY.md`](DEPLOY.md).
