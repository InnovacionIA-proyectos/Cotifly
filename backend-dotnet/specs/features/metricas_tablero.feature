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

  # HU-021.10 (#137), decisión 8: los conteos son por cotización; los valores y la apertura por línea de negocio, por ruta.
  # Las cotizaciones de los antecedentes tienen una ruta "Nacional".
  Escenario: Una cotización ganada con rutas de tres líneas de negocio cuenta una vez y se abre por ruta
    Dado que la cotización "A1" tiene estas rutas:
      | orden | linea       | costo  | precio | margen |
      | 1     | Nacional    | 300000 | 350000 | 0.1429 |
      | 2     | Importacion | 250000 | 300000 | 0.1667 |
      | 3     | Exportacion | 300000 | 350000 | 0.1429 |
    Cuando "ana" consulta sus métricas del "2026-01-01" al "2026-12-31"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "MetricasComercial" del contrato
    Y los indicadores son:
      | campo              | valor   |
      | cantidad           | 4       |
      | ganadas            | 2       |
      | costeo_total       | 5950000 |
      | valor_ganado_cop   | 5000000 |
    Y la apertura por línea de negocio es:
      | linea       | cotizaciones | ganadas | rutas | costeo_total | valor_cotizado_cop | ganancias |
      | Nacional    | 4            | 2       | 4     | 5400000      |                    |           |
      | Importacion | 1            | 1       | 1     | 250000       | 300000             | 50000     |
      | Exportacion | 1            | 1       | 1     | 300000       | 350000             | 50000     |

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

  # HU-037.3 (#211): el director ve todas las regionales (o la que elige) y filtra un comercial de cualquiera; el gerente solo la suya.
  Escenario: El director ve el tablero de la regional que elige
    Dado el director "dario" con sesión en la regional "Sur"
    Cuando "dario" consulta las métricas de la regional "Occidente" del "2026-01-01" al "2026-12-31"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "MetricasRegional" del contrato
    Y la regional de las métricas es "Occidente"
    Y los comerciales de las métricas son "ana", "luis" y "gloria"
    Y los indicadores son:
      | campo            | valor   |
      | cantidad         | 5       |
      | ganadas          | 3       |
      | valor_ganado_cop | 5500000 |

  Escenario: El director sin regional ve todas y filtra por un comercial de cualquier regional
    Dado el director "dario" con sesión en la regional "Sur"
    Cuando "dario" consulta las métricas de su regional del "2026-01-01" al "2026-12-31" filtradas por "marta"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "MetricasRegional" del contrato
    Y las métricas son de todas las regionales
    Y los indicadores son:
      | campo            | valor   |
      | cantidad         | 1       |
      | ganadas          | 1       |
      | valor_ganado_cop | 9000000 |
    Y la lista de comerciales de las métricas incluye a "ana", "marta" y "dario"

  Escenario: El gerente no puede pedir el tablero de otra regional, pero sí la suya
    Cuando "gloria" consulta las métricas de la regional "Norte" del "2026-01-01" al "2026-12-31"
    Entonces la respuesta tiene el estado 403 con el código de negocio "REGIONAL_NO_PERMITIDA"
    Cuando "gloria" consulta las métricas de la regional "Occidente" del "2026-01-01" al "2026-12-31"
    Entonces la respuesta tiene el estado 200
    Y la regional de las métricas es "Occidente"

  Escenario: Un comercial no puede ver las métricas de la regional
    Cuando "ana" consulta las métricas de su regional del "2026-01-01" al "2026-12-31"
    Entonces la respuesta tiene el estado 403 con el error "No tienes permiso para esta operación."

  Escenario: Un gerente sin regional asignada recibe un error claro
    Dado el gerente comercial "hugo" con sesión y sin regional
    Cuando "hugo" consulta las métricas de su regional del "2026-01-01" al "2026-12-31"
    Entonces la respuesta tiene el estado 409 con el código de negocio "REGIONAL_NO_ASIGNADA"

  # HU-010.5 (#187): filtro por año y mes en el tablero del gerente. Fechas de America/Bogota; anio reemplaza a desde/hasta.
  Escenario: El gerente filtra el tablero por un año completo
    Cuando "gloria" consulta las métricas de su regional del año "2026"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "MetricasRegional" del contrato
    Y el periodo de las métricas va del "2026-01-01" al "2026-12-31"
    Y los indicadores son:
      | campo    | valor |
      | cantidad | 5     |
      | ganadas  | 3     |
    Y la serie mensual tiene el mes "2026-12" con 0 cotizaciones y 0 ganadas

  Escenario: El gerente filtra el tablero por un mes completo
    Cuando "gloria" consulta las métricas de su regional del mes "4" de "2026"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "MetricasRegional" del contrato
    Y el periodo de las métricas va del "2026-04-01" al "2026-04-30"
    Y los indicadores son:
      | campo    | valor |
      | cantidad | 3     |
      | ganadas  | 2     |
    Y la serie mensual tiene el mes "2026-04" con 3 cotizaciones y 2 ganadas

  Escenario: El año y el mes reemplazan a desde y hasta
    Cuando "gloria" consulta las métricas de su regional con "anio=2026&mes=3&desde=2026-04-01&hasta=2026-04-30"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "MetricasRegional" del contrato
    Y el periodo de las métricas va del "2026-03-01" al "2026-03-31"
    Y los indicadores son:
      | campo    | valor |
      | cantidad | 2     |

  Escenario: Los límites del mes son los de Bogotá y no los de UTC
    Dado que "luis" tiene estas cotizaciones:
      | cotizacion | moneda | precio | trm | costo  | margen | resultado | creada                    |
      | L2         | COP    | 100000 | 0   | 80000  | 0.20   | ganada    | 2026-04-30T23:30:00-05:00 |
      | L3         | COP    | 100000 | 0   | 80000  | 0.20   | perdida   | 2026-05-01T00:30:00-05:00 |
    Cuando "gloria" consulta las métricas de su regional del mes "4" de "2026"
    Entonces los indicadores son:
      | campo    | valor |
      | cantidad | 4     |
    Cuando "gloria" consulta las métricas de su regional del mes "5" de "2026"
    Entonces los indicadores son:
      | campo    | valor |
      | cantidad | 1     |
      | perdidas | 1     |

  Esquema del escenario: Un año o un mes inválidos responden 400
    Cuando "gloria" consulta las métricas de su regional con "<consulta>"
    Entonces la respuesta tiene el estado 400 con un ErrorWeb

    Ejemplos:
      | consulta              |
      | mes=4                 |
      | anio=2026&mes=0       |
      | anio=2026&mes=13      |
      | anio=2026&mes=abril   |
      | anio=2019             |
      | anio=2099             |
      | anio=dosmil           |

  Escenario: El tablero del comercial no cambia con año y mes
    Cuando "ana" consulta sus métricas con "desde=2026-04-01&hasta=2026-04-30&anio=2025&mes=99"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "MetricasComercial" del contrato
    Y el periodo de las métricas va del "2026-04-01" al "2026-04-30"
    Y los indicadores son:
      | campo    | valor |
      | cantidad | 2     |

  # HU-034.1 (#201), parte B: ganancias y costeo por mes en serie_mensual, con la regla de los indicadores (COP o USD × TRM, sin
  # rutas rechazadas, versión vigente y lo ganado de la versión ganada, mes de Bogotá). La suma de los meses cuadra con los
  # indicadores del periodo.
  Escenario: La serie mensual del gerente trae las ganancias y el costeo de cada mes
    Cuando "gloria" consulta las métricas de su regional del año "2026"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "MetricasRegional" del contrato
    Y la serie mensual tiene estos montos:
      | mes     | cantidad | ganadas | valor_cotizado_cop | valor_ganado_cop | costeo_total | costeo_ganadas | ganancias |
      | 2026-03 | 2        | 1       | 3000000            | 1000000          | 2550000      | 850000         | 150000    |
      | 2026-04 | 3        | 2       | 4500000            | 4500000          | 3800000      | 3800000        | 700000    |
      | 2026-05 | 0        | 0       | 0                  | 0                | 0            | 0              | 0         |
    Y la suma de la serie mensual cuadra con los indicadores

  Escenario: Las ganancias de cada mes usan el mes de Bogotá y el filtro por comercial
    Dado que "luis" tiene estas cotizaciones:
      | cotizacion | moneda | precio | trm | costo | margen | resultado | creada                    |
      | L2         | COP    | 100000 | 0   | 80000 | 0.20   | ganada    | 2026-04-30T23:30:00-05:00 |
      | L3         | COP    | 100000 | 0   | 80000 | 0.20   | ganada    | 2026-05-01T00:30:00-05:00 |
    Cuando "gloria" consulta las métricas de su regional del "2026-04-01" al "2026-05-31" filtradas por "luis"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "MetricasRegional" del contrato
    Y la serie mensual tiene estos montos:
      | mes     | cantidad | ganadas | costeo_total | costeo_ganadas | ganancias |
      | 2026-04 | 2        | 2       | 480000       | 480000         | 120000    |
      | 2026-05 | 1        | 1       | 80000        | 80000          | 20000     |
    Y la suma de la serie mensual cuadra con los indicadores

  Escenario: En la serie del comercial una ruta rechazada no cuenta y una cotización rehecha suma su versión vigente
    Dado que la cotización "A1" tiene estas rutas:
      | orden | linea       | costo  | precio | margen |
      | 1     | Nacional    | 300000 | 350000 | 0.1429 |
      | 2     | Importacion | 250000 | 300000 | 0.1667 |
    Y que la Mesa de Costeo rechazó la ruta 2 de la cotización "A1"
    Y que "ana" rehízo la cotización "A2" como versión 2 con precio 2100000 y costo 1750000
    Cuando "ana" consulta sus métricas del "2026-01-01" al "2026-12-31"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "MetricasComercial" del contrato
    Y la serie mensual tiene estos montos:
      | mes     | cantidad | ganadas | perdidas | valor_cotizado_cop | valor_ganado_cop | costeo_total | costeo_ganadas | ganancias |
      | 2026-03 | 2        | 1       | 1        | 2450000            | 350000           | 2050000      | 300000         | 50000     |
      | 2026-04 | 2        | 1       | 0        | 4000000            | 4000000          | 3400000      | 3400000        | 600000    |
    Y la suma de la serie mensual cuadra con los indicadores
