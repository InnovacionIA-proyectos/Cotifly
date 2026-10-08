# 9. Estructura del proyecto

```
.github/workflows/
└── backend-dotnet.yml           # CI del backend .NET (#14): unitarias, integración, cobertura, auditoría NuGet, imagen, lint OpenAPI

backend-dotnet/
├── Cotifly.sln
├── README.md                    # punto de entrada e índice de la documentación
├── Dockerfile                   # imagen multi-stage de Cotifly.Api (#27, ADR-007)
├── .dockerignore                # lista blanca del contexto: props, global.json, .editorconfig y src/
├── global.json                  # SDK 10.0.x y runner Microsoft.Testing.Platform
├── Directory.Build.props        # net10.0, Nullable, warnings como errores, analizadores
├── Directory.Packages.props     # versiones NuGet centralizadas (CPM)
├── .editorconfig                # convenciones de nombres y namespaces (error de build)
├── .gitattributes               # finales de línea LF, binarios
├── .gitignore                   # bin/obj, resultados de pruebas y cobertura, .env y .env.*
├── .env.example                 # variables de entorno documentadas
├── docs/                        # documentación técnica (este conjunto, 01 a 10)
│   └── qa/                      # regresión manual (HU-003.8) y plantilla de paridad (HU-003.9)
├── scripts/
│   ├── levantar-local.ps1       # entorno local sin compose: SQL Server + esquema + API (PowerShell)
│   ├── levantar-local.sh        # lo mismo para Linux, macOS o Git Bash
│   ├── verificar-cobertura.sh   # umbral de cobertura por ensamblado (lo usa el CI)
│   ├── contar-pruebas-trx.sh    # mínimo de pruebas ejecutadas según los TRX (lo usa el CI)
│   └── sql/                     # SOLO desarrollo: bases, dbsolucionesti falsa, IDROLES, login cotifly_app y verificación de permisos
├── specs/
│   ├── openapi.yaml             # contrato HTTP (#17)
│   ├── features/                # escenarios Gherkin (Reqnroll): persistencia, precio, margen, pdf, correo, auditoria,
│   │                            # login_clientepro, refresh_logout, folio, aislamiento_comercial, web_cotizaciones,
│   │                            # admin_lineas_negocio, asistente_folio, validador_respuesta_ia, costeo y licitaciones
│   │                            # (chat_slot_filling, chat_web, chat_municipios y google_chat se borraron en HU-013.9)
│   └── decisiones/              # ADR-001 … ADR-012
├── src/
│   ├── Cotifly.Dominio/         # Comun/ + entidades, value objects y enums por módulo; Sesiones/
│   ├── Cotifly.Aplicacion/      # Comun/{Interfaces,Exceptions}/ + <Módulo>/{DTOs,Validators,Services,Repositories,Interfaces}/
│   │                            # Agente/{Herramientas,Filtros,Recursos}/ (asistente por folio) + clsDependenciasAplicacion
│   ├── Cotifly.Infraestructura/ # Comun/Persistence/ (DbContext, configuraciones EF, UoW, interceptor, candados, health check),
│   │                            # Configuracion/, <Módulo>/{Repositories,Services}/, Identidad/Persistence/, IA/Services/,
│   │                            # recursos del PDF + clsDependenciasInfraestructura
│   └── Cotifly.Api/             # Program.cs (+ rama --healthcheck), Sistema/ (health y sonda), Comun/ (errores, JSON web,
│                                # seguridad HTTP) + <Módulo>/{Controllers,DTOs}/ de los módulos con endpoints
└── tests/
    ├── Directory.Build.props    # xUnit v3, AwesomeAssertions, TRX y cobertura; Testcontainers y soporte con UsaSqlServerPruebas
    ├── cobertura.config.xml     # ensamblados medidos por la cobertura (#14)
    ├── Comun/                   # soporte compartido de integración (enlazado, no es proyecto): SQL Server, reloj, SMTP, SAP,
    │                            # Nexo, TRM y modelo de IA falsos, lector de PDF
    ├── fixtures/sap/            # muestra de RentabClienteSet portada del Python
    ├── fixtures/cotizacion/     # ejemplo-solicitud.json del Python (payload del PDF, #11)
    ├── artefactos/              # PDF de la comparación visual que generan las pruebas (#11, no versionado)
    ├── Cotifly.Dominio.Tests/
    ├── Cotifly.Aplicacion.Tests/
    ├── Cotifly.Infraestructura.Tests/
    ├── Cotifly.Api.Tests/
    ├── Cotifly.Arquitectura.Tests/
    └── Cotifly.Aceptacion.Tests/

db/migrations_mssql/                # scripts de esquema SQL Server (0001 a 0005): única vía de cambio de esquema
docs/docs_front/CONTRATO_API_WEB.md         # resumen del contrato para el frontend (HU-004.1)
docs/conexion_nexo/MAPEO_CAMPOS_NEXO_COTIFLY.md # mapeo de campos Nexo ↔ Cotifly del formulario de captura (HU-005)
```

## 9.1 Dónde va cada cosa

| Necesito... | Va en |
|---|---|
| Una regla de negocio pura (sin E/S) | `Cotifly.Dominio/<Módulo>/` con su prueba en `Cotifly.Dominio.Tests` |
| Un caso de uso o un puerto | `Cotifly.Aplicacion/<Módulo>/Services/` y `Interfaces/` o `Repositories/` (puerto `ICls*`) |
| Un adaptador (base, HTTP, SDK) | `Cotifly.Infraestructura/<Módulo>/Services/` o `Repositories/`, registrado en `clsDependenciasInfraestructura` |
| Un endpoint | Primero `specs/openapi.yaml`; luego `Cotifly.Api/<Módulo>/Controllers/` heredando de `clsControladorWebBase` si es `/web/*` |
| Una herramienta del asistente | `Cotifly.Aplicacion/Agente/Herramientas/clsHerramientasAsistenteFolio` (solo lectura o propuesta; la cotización sale de `clsContextoAsistenteFolio`, nunca del LLM) + la propuesta en `clsPropuestasAsistente` con las reglas del endpoint destino |
| Un cambio de esquema | Script nuevo `db/migrations_mssql/000N_*.sql` idempotente (nunca migraciones EF), mapeo en `Comun/Persistence/Configurations/` y prueba de deriva |
| Una opción de configuración | Clase `clsOpcionesXxx` con `AddOpcionesValidadas<T>()`, variable en `.env.example` y fila en [03-infraestructura](03-infraestructura.md#32-variables-de-entorno-y-configuración) |
| Una decisión de arquitectura | ADR nueva en `specs/decisiones/` con la plantilla del índice |

Convenciones de nombres y de carpetas: [02-arquitectura](02-arquitectura.md#24-convenciones-de-nombres).
