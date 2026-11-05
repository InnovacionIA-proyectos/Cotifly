# 9. Estructura del proyecto

> **Estado:** vigente · **Dueño:** Duban Guerra (backend) · **Actualizado:** 2026-10-15

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
│   └── qa/                      # regresión manual (HU-003.8), plantilla de paridad (HU-003.9) y matriz de pruebas de HU-021
├── scripts/
│   ├── levantar-local.ps1       # entorno local sin compose: SQL Server + esquema + API (PowerShell)
│   ├── levantar-local.sh        # lo mismo para Linux, macOS o Git Bash
│   ├── correr-tests.ps1         # pruebas por área funcional o la suite completa (PowerShell)
│   ├── correr-tests.sh          # lo mismo para Linux, macOS o Git Bash
│   ├── verificar-cobertura.sh   # umbral de cobertura por ensamblado (lo usa el CI)
│   ├── contar-pruebas-trx.sh    # mínimo de pruebas ejecutadas según los TRX (lo usa el CI)
│   ├── publicar-imagen.ps1/.sh  # construye, prueba, escanea y publica la imagen (HU-006.4)
│   ├── verificar-manifiestos-dotnet.sh # valida los manifiestos de Swarm del backend .NET (HU-006.6)
│   ├── respaldo/                # exportación y restore de dbo_cotifly (HU-006.7)
│   └── sql/                     # SOLO desarrollo: bases, dbsolucionesti falsa, IDROLES, login cotifly_app y verificación de permisos
├── specs/
│   ├── openapi.yaml             # contrato HTTP (#17)
│   ├── features/                # escenarios Gherkin (Reqnroll), 30 archivos: persistencia, precio, margen, pdf, correo,
│   │                            # auditoria, login, folio, web, costeo y licitaciones, y los de HU-021: captura_rutas,
│   │                            # integracion_cubicador, envio_nexo_por_camion, margen_conexos_por_ruta, envio_conexos_nexo,
│   │                            # margen_sap_anomalo, metricas_tablero (chat_* y google_chat se borraron en HU-013.9)
│   └── decisiones/              # ADR-001 … ADR-019 (índice en README.md)
├── src/
│   ├── Cotifly.Dominio/         # Comun/ + entidades, value objects y enums por módulo; Sesiones/. Cotizaciones/ tiene el
│   │                            # agregado de HU-021: clsCotizacionSolicitud → clsLineaSolicitud → clsVehiculoLineaSolicitud
│   ├── Cotifly.Aplicacion/      # Comun/{Interfaces,Exceptions}/ + <Módulo>/{DTOs,Validators,Services,Repositories,Interfaces}/
│   │                            # Agente/{Herramientas,Filtros,Recursos}/ (asistente por folio) + clsDependenciasAplicacion.
│   │                            # Módulos sin carpeta en Dominio: Costeo, Licitaciones, Redaccion, Municipios, CatalogosFlete
│   │                            # y Cubicacion (Nexo y el cubicador de HU-021.5)
│   ├── Cotifly.Infraestructura/ # Comun/Persistence/ (DbContext, configuraciones EF, UoW, interceptor, candados, health check),
│   │                            # Configuracion/, <Módulo>/{Repositories,Services}/, Identidad/Persistence/, IA/Services/,
│   │                            # recursos del PDF + clsDependenciasInfraestructura
│   └── Cotifly.Api/             # Program.cs (+ rama --healthcheck), Sistema/ (health y sonda), Comun/ (errores, JSON web,
│                                # seguridad HTTP) + <Módulo>/{Controllers,DTOs}/ de los módulos con endpoints
└── tests/
    ├── Directory.Build.props    # xUnit v3, AwesomeAssertions, TRX y cobertura; Testcontainers y soporte con UsaSqlServerPruebas
    ├── cobertura.config.xml     # ensamblados medidos por la cobertura (#14)
    ├── Comun/                   # soporte compartido de integración (enlazado, no es proyecto): SQL Server, reloj, MailSender,
    │                            # muestra de SAP TM, Nexo, cubicador, TRM y modelo de IA falsos, lector de PDF
    ├── fixtures/cotizacion/     # ejemplo-solicitud.json del Python (payload del PDF, #11)
    ├── fixtures/licitaciones/   # pliego de 300 líneas (paridad con el lector de Nexo, HU-011)
    ├── fixtures/pdf/            # referencia de la maquetación del PDF
    ├── artefactos/              # PDF de la comparación visual que generan las pruebas (#11, no versionado)
    ├── Cotifly.Dominio.Tests/
    ├── Cotifly.Aplicacion.Tests/
    ├── Cotifly.Infraestructura.Tests/
    ├── Cotifly.Api.Tests/
    ├── Cotifly.Arquitectura.Tests/
    └── Cotifly.Aceptacion.Tests/

db/
├── README.md                       # instalar desde cero, migrar una base existente y verificar
├── ejecutar-bd.ps1, ejecutar-bd.sh # ejecutador: crea la base (opcional), corre scripts/ y luego seeder/
├── scripts/01_crear_esquema.sql    # instalación desde cero: el esquema final (= 0001 a 0010 aplicados)
├── seeder/                         # 01_lineas_negocio, 02_sap_mapeo_trqtype, 03_roles
└── migrations_mssql/               # migraciones 0001 a 0010 para bases que ya existen (+ rollback/): única vía de cambio
                                    # de esquema; las leen el backend, las pruebas y el CI
BASE_DE_DATOS.md                    # punto de entrada a la base de datos (HU-021)
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
| Un cambio de esquema | Script nuevo `db/migrations_mssql/000N_*.sql` idempotente (nunca migraciones EF), el mismo cambio en `db/scripts/01_crear_esquema.sql` (`clsEsquemaTests` exige que las dos vías dejen el mismo esquema), el script en `tests/Comun/clsBaseDatosPruebas.Scripts`, mapeo en `Comun/Persistence/Configurations/` y prueba de deriva. Pendiente HU-023: `db/scripts/02_alter_cubicador.sql` |
| Una opción de configuración | Clase `clsOpcionesXxx` con `AddOpcionesValidadas<T>()`, variable en `.env.example` y fila en [03-infraestructura](03-infraestructura.md#32-variables-de-entorno-y-configuración) |
| Una decisión de arquitectura | ADR nueva en `specs/decisiones/` con la plantilla del índice |

Convenciones de nombres y de carpetas: [02-arquitectura](02-arquitectura.md#24-convenciones-de-nombres).
