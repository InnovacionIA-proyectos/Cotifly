# language: es
# HU-010.1 (#45) — Métricas agregadas del tablero (ADR-016). Por HTTP contra la API real en memoria, con sesiones del login
# SSO (el rol sale del claim `rol` del token) y SQL Server 2019 en Testcontainers. La regional se fija en
# UsuariosWeb.regional (respaldo del DBA; DimEjecutivo no está configurada en estas pruebas). Los nombres de comerciales,
# regionales y cotizaciones son alias: cada escenario crea los suyos. Las fechas son de America/Bogota. Una versión nueva
# ("rehízo") es una fila con el folio -vN, creada un día después y sin resultado, como la crea la consola.
@integracion
Característica: Tablero de métricas del comercial y del gerente comercial
  Como gerente comercial de una regional
  Quiero ver las métricas de mi regional y poder filtrarlas por comercial
  Para monitorear la conversión y la salud de las cotizaciones sin reportes externos

  Antecedentes:
    Dado la API web de Cotifly
    Y el comercial "ana" con sesión en la regional "Occidente"
    Y el comercial "luis" con sesión en la regional "Occidente"
    Y el comercial "marta" con sesión en la regional "Norte"
    Y el gerente comercial "gloria" con sesión en la regional "Occidente"
    Y que "ana" tiene estas cotizaciones:
      | cotizacion | moneda | precio  | trm  | costo   | margen | resultado | creada     |
      | A1         | COP    | 1000000 | 0    | 850000  | 0.15   | ganada    | 2026-03-10 |
      | A2         | COP    | 2000000 | 0    | 1700000 | 0.15   | perdida   | 2026-03-20 |
      | A3         | USD    | 1000    | 4000 | 3400000 | 0.15   | ganada    | 2026-04-05 |
      | A4         | COP    |         | 0    |         |        |           | 2026-04-06 |
    Y que "luis" tiene estas cotizaciones:
      | cotizacion | moneda | precio  | trm  | costo   | margen | resultado | creada     |
      | L1         | COP    | 500000  | 0    | 400000  | 0.20   | ganada    | 2026-04-15 |
    Y que "marta" tiene estas cotizaciones:
      | cotizacion | moneda | precio  | trm  | costo   | margen | resultado | creada     |
      | M1         | COP    | 9000000 | 0    | 8000000 | 0.10   | ganada    | 2026-04-15 |

  Escenario: El comercial ve solo las métricas de sus cotizaciones, con USD convertido a pesos
    Cuando "ana" consulta sus métricas del "2026-01-01" al "2026-12-31"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "MetricasComercial" del contrato
    Y los indicadores son:
      | campo                     | valor      |
      | cantidad                  | 4          |
      | ganadas                   | 2          |
      | perdidas                  | 1          |
      | sin_resultado             | 0          |
      | pendientes_por_cotizar    | 1          |
      | conversion                | 0.5        |
      | conversion_sobre_cerradas | 0.6667     |
      | margen_promedio           | 0.15       |
      | costeo_total              | 5950000    |
      | costeo_ganadas            | 4250000    |
      | valor_cotizado_cop        | 7000000    |
      | valor_ganado_cop          | 5000000    |
      | ganancias                 | 750000     |
      | cotizaciones_usd          | 1          |
      | deadline_vencidas         | 0          |
    Y los indicadores cumplen cantidad = ganadas + perdidas + sin_resultado + pendientes_por_cotizar

  Escenario: Una cotización rehecha cuenta una sola vez, con los valores de su versión vigente y el resultado de la cadena
    Dado que "ana" rehízo la cotización "A2" como versión 2 con precio 2100000 y costo 1750000
    Cuando "ana" consulta sus métricas del "2026-01-01" al "2026-12-31"
    Entonces la respuesta tiene el estado 200
    Y los indicadores son:
      | campo              | valor   |
      | cantidad           | 4       |
      | versiones          | 5       |
      | valor_cotizado_cop | 7100000 |
      | costeo_total       | 6000000 |
      | perdidas           | 1       |

  Escenario: El periodo filtra por la fecha de creación en Bogotá
    Cuando "ana" consulta sus métricas del "2026-04-01" al "2026-04-30"
    Entonces la respuesta tiene el estado 200
    Y los indicadores son:
      | campo    | valor |
      | cantidad | 2     |
      | ganadas  | 1     |
    Y la serie mensual tiene el mes "2026-04" con 2 cotizaciones y 1 ganadas

  Escenario: Fechas inválidas responden 400
    Cuando "ana" consulta sus métricas del "2026-12-31" al "2026-01-01"
    Entonces la respuesta tiene el estado 400 con un ErrorWeb

  Escenario: El gerente comercial ve el agregado de su regional y no el de otras
    Cuando "gloria" consulta las métricas de su regional del "2026-01-01" al "2026-12-31"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "MetricasRegional" del contrato
    Y la regional de las métricas es "Occidente"
    Y los comerciales de las métricas son "ana", "luis" y "gloria"
    Y en la lista de comerciales "gloria" tiene el rol "gerente_comercial" y "ana" el rol "comercial"
    Y los indicadores son:
      | campo            | valor   |
      | cantidad         | 5       |
      | ganadas          | 3       |
      | valor_ganado_cop | 5500000 |

  Escenario: El gerente filtra el tablero por un comercial de su regional
    Cuando "gloria" consulta las métricas de su regional del "2026-01-01" al "2026-12-31" filtradas por "luis"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "MetricasRegional" del contrato
    Y los indicadores son:
      | campo     | valor  |
      | cantidad  | 1      |
      | ganadas   | 1      |
      | ganancias | 100000 |
    Y los comerciales de las métricas son "ana", "luis" y "gloria"

  Escenario: Filtrar por un comercial de otra regional responde 404
    Cuando "gloria" consulta las métricas de su regional del "2026-01-01" al "2026-12-31" filtradas por "marta"
    Entonces la respuesta tiene el estado 404 con el error "Comercial no encontrado en tu regional."

  Escenario: Un comercial no puede ver las métricas de la regional
    Cuando "ana" consulta las métricas de su regional del "2026-01-01" al "2026-12-31"
    Entonces la respuesta tiene el estado 403 con el error "No tienes permiso para esta operación."

  Escenario: Un gerente sin regional asignada recibe un error claro
    Dado el gerente comercial "hugo" con sesión y sin regional
    Cuando "hugo" consulta las métricas de su regional del "2026-01-01" al "2026-12-31"
    Entonces la respuesta tiene el estado 409 con el código de negocio "REGIONAL_NO_ASIGNADA"
