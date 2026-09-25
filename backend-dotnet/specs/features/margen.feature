# language: es
# HU-003.4 (#10) — Resolución del margen y margen negociado (ADR-004, ADR-008, ADR-010).
# Casos de tests/test_margen.py del Python contra SQL Server 2019 (Testcontainers) con la semilla del script
# 0001 (8 líneas al 15 %, TrqType OPTN…OPSC) y la muestra de SAP TM del Python
# (tests/fixtures/sap/rentab_cliente_muestra.json) como fuente en memoria: los adaptadores reales
# export_json | odata llegan con #13. Márgenes entre comillas (fracción decimal con punto).
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
    Cuando pido el margen de la línea "Almacenamiento" para el BP "3301" en la organización "50000010"
    Entonces el margen es "0.15" con origen "sap_tm" y TrqType "OHUB"

  Esquema del escenario: La línea se reconoce sin importar tildes, mayúsculas ni espacios
    Cuando pido el margen de la línea "<escrita>" sin BP
    Entonces la línea resuelta es "<linea>" con TrqType "<trq>"

    Ejemplos:
      | escrita            | linea             | trq  |
      | Distribución       | Distribucion      | OPDS |
      | IMPORTACIÓN        | Importacion       | OPIM |
      | almacenamiento3pl  | Almacenamiento3PL | OH3P |
      | ALMACENAMIENTO 3PL | Almacenamiento3PL | OH3P |

  Escenario: Una línea desconocida se rechaza
    Cuando pido el margen de la línea "Aérea" para el BP "15927"
    Entonces la operación de margen se rechaza con el código "MARGEN_LINEA_DESCONOCIDA"

  Escenario: SAP TM configurado pero caído
    Dado que SAP TM no responde
    Cuando pido el margen de la línea "Nacional" para el BP "15927"
    Entonces la operación de margen se rechaza con el código "MARGEN_FUENTE_NO_DISPONIBLE"

  Escenario: SAP TM todavía no configurado (hasta #13)
    Dado que SAP TM no está configurado
    Cuando pido el margen de la línea "Nacional" para el BP "15927"
    Entonces el margen es "0.15" con origen "politica_linea" y TrqType "OPTN"
    Y SAP TM no fue consultado
    Y el detalle menciona "SAP TM no configurado: se usa la política de la línea"

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
