# language: es
# HU-003.4 (#10), HU-003.7 (#13), HU-042.2 (#243) y HU-042.4 (#247) — Resolución del margen y margen negociado (ADR-004, ADR-008,
# ADR-010, ADR-027). Casos de tests/test_margen.py del Python contra SQL Server 2019 (Testcontainers) con la semilla del script 0001
# (HU-021: el 0009 retira Almacenamiento, Almacenamiento3PL e ITR y agrega DevolucionContenedor) y la muestra de SAP TM del Python como
# fuente en memoria. Los escenarios "copia local" usan la fuente real de HU-042.2: dbo_cotifly.SapMargenCliente, que sincroniza a diario
# el job de Datos (HU-042.1); Cotifly ya no llama a SAP TM al cotizar. Márgenes entre comillas (fracción decimal con punto).
@integracion
Característica: Resolución del margen de una cotización
  Como comercial de Botero Soto
  Quiero que el margen salga de SAP TM para el cliente o, si no aplica, de la política de la línea
  Para cotizar con el margen correcto y poder negociarlo cuando haga falta

  Antecedentes:
    Dado la política de margen sembrada por el script 0001
    Y la fuente SAP TM con la muestra de rentabilidad por cliente

  Escenario: Cliente con registro en SAP TM
    Cuando pido el margen de la línea "Nacional" para el BP "15927"
    Entonces el margen es "0.135" con origen "sap_tm" y TrqType "OPTN"
    Y el margen no está marcado como anómalo
    Y el detalle menciona "15927"

  Escenario: Sin BP del cliente no se consulta SAP TM
    Cuando pido el margen de la línea "Nacional" sin BP
    Entonces el margen es "0.15" con origen "politica_linea" y TrqType "OPTN"
    Y SAP TM no fue consultado
    Y el detalle menciona "cliente nuevo: sin BP de SAP"

  Escenario: Cliente sin registro en SAP TM
    Cuando pido el margen de la línea "Nacional" para el BP "99999"
    Entonces el margen es "0.15" con origen "politica_linea" y TrqType "OPTN"
    Y el detalle menciona "sin registro en SAP TM"

  Esquema del escenario: Un margen de SAP TM fuera de [0, 60 %] se marca anómalo
    Cuando pido el margen de la línea "<linea>" para el BP "<bp>"
    Entonces el margen es "<margen>" con origen "sap_tm" y TrqType "<trq>"
    Y el margen está marcado como anómalo

    Ejemplos:
      | linea       | bp   | margen | trq  |
      | Nacional    | 5000 | -0.192 | OPTN |
      | Importacion | 5001 | 0.999  | OPIM |

  Escenario: Se respeta la organización de ventas
    Cuando pido el margen de la línea "Nacional" para el BP "15927" en la organización "50000005"
    Entonces el margen es "0.135" con origen "sap_tm" y TrqType "OPTN"

  Esquema del escenario: La línea se reconoce sin importar tildes, mayúsculas ni espacios
    Cuando pido el margen de la línea "<escrita>" sin BP
    Entonces la línea resuelta es "<linea>" con TrqType "<trq>"

    Ejemplos:
      | escrita            | linea             | trq  |
      | Distribución       | Distribucion      | OPDS |
      | IMPORTACIÓN        | Importacion       | OPIM |
      | nacional           | Nacional          | OPTN |
      |   EXPORTACIÓN      | Exportacion       | OPEX |

  Escenario: Una línea desconocida se rechaza
    Cuando pido el margen de la línea "Aérea" para el BP "15927"
    Entonces la operación de margen se rechaza con el código "MARGEN_LINEA_DESCONOCIDA"

  # Decisión de #13, alineada con HU-009 ("Resiliencia si SAP no responde"): la cotización no se interrumpe.
  Escenario: SAP TM configurado pero caído usa la política de la línea
    Dado que SAP TM no responde
    Cuando pido el margen de la línea "Nacional" para el BP "15927"
    Entonces el margen es "0.15" con origen "politica_linea" y TrqType "OPTN"
    Y el detalle menciona "SAP TM no disponible: se usa la política de la línea"

  # HU-042.2: "no configurado" es una copia local que nunca se sincronizó (ningún 'ok' del job), como en un ambiente sin el job de Datos.
  Escenario: SAP TM sin configurar (la copia local nunca se sincronizó)
    Dado que SAP TM no está configurado
    Cuando pido el margen de la línea "Nacional" para el BP "15927"
    Entonces el margen es "0.15" con origen "politica_linea" y TrqType "OPTN"
    Y SAP TM no fue consultado
    Y el detalle menciona "SAP TM no configurado: se usa la política de la línea"

  # HU-042.2 (#243, ADR-027): cada cliente recibe SU margen de la copia local (no el de la primera fila del TrqType), el BP se compara
  # sin ceros a la izquierda y el detalle dice de dónde salió y cuándo cambió la fila. origen sigue siendo sap_tm.
  Escenario: El margen de SAP TM se lee de la copia local para el cliente pedido
    Dado la copia local de SAP TM sincronizada hace 2 horas
    Y en la copia local el cliente "880" tiene margen "21.25" % en "OPTN"
    Cuando pido el margen de la línea "Nacional" para el BP "0000000880"
    Entonces el margen es "0.2125" con origen "sap_tm" y TrqType "OPTN"
    Y el margen no está marcado como anómalo
    Y el detalle menciona "Margen propuesto SAP TM (tabla local, actualizado 2026-09-23) para cliente 0000000880 en OPTN: 21.25%"
    Y el detalle no menciona "sincronización con más de"

  Escenario: Con la copia local vieja el margen se usa igual y el detalle lo avisa
    Dado la copia local de SAP TM sincronizada hace 72 horas
    Y en la copia local el cliente "881" tiene margen "12" % en "OPTN"
    Cuando pido el margen de la línea "Nacional" para el BP "881"
    Entonces el margen es "0.12" con origen "sap_tm" y TrqType "OPTN"
    Y el detalle menciona "sincronización con más de 48 h"

  Escenario: Una fila inactiva de la copia local no se usa
    Dado la copia local de SAP TM sincronizada hace 2 horas
    Y en la copia local el cliente "882" está inactivo en "OPEX"
    Cuando pido el margen de la línea "Exportacion" para el BP "882"
    Entonces el margen es "0.15" con origen "politica_linea" y TrqType "OPEX"
    Y el detalle menciona "sin registro en SAP TM"

  Escenario: Un BP con comillas no altera la consulta a la copia local
    Dado la copia local de SAP TM sincronizada hace 2 horas
    Y en la copia local el cliente "883" tiene margen "30" % en "OPTN"
    Cuando pido el margen de la línea "Nacional" para el BP "883' OR '1'='1"
    Entonces el margen es "0.15" con origen "politica_linea" y TrqType "OPTN"
    Y el detalle menciona "sin registro en SAP TM"

  Escenario: El comercial negocia un margen distinto al sugerido
    Dado el comercial "ana.negocia" registrado
    Cuando "ana.negocia" negocia el margen "0.12" sobre el sugerido "0.15" con el método "margen" para un costo de 2850000
    Entonces el margen queda en "0.12" con origen "negociado"
    Y el precio se calcula con el margen negociado: 3238636
    Y queda registrado un ajuste de "ana.negocia" con diferencia "-0.03"

  Escenario: Con markup el margen negociado puede ser mayor o igual a 1
    Dado el comercial "ana.negocia" registrado
    Cuando "ana.negocia" negocia el margen "1.2" sobre el sugerido "0.8" con el método "markup" para un costo de 1000000
    Entonces el margen queda en "1.2" con origen "negociado"
    Y el precio se calcula con el margen negociado: 2200000

  Escenario: Con método margen un margen negociado de 1 o más se rechaza
    Dado el comercial "ana.negocia" registrado
    Cuando "ana.negocia" negocia el margen "1" sobre el sugerido "0.15" con el método "margen" para un costo de 2850000
    Entonces la operación de margen se rechaza con el código "AJUSTE_MARGEN_INVALIDO"
