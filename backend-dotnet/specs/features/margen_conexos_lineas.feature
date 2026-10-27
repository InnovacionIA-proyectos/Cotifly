# language: es
# HU-041.1 (#231, ADR-026) — Margen y servicios conexos para varias rutas en UNA operación: PUT /web/cotizaciones/{folio}/costeo/margen/lineas
# y POST /web/cotizaciones/{folio}/costeo/servicios-conexos/lineas. Las mismas reglas que por ruta, pero atómicas: si una ruta no admite el
# cambio no cambia ninguna y el error dice cuál. Una invalidación del PDF y una auditoría por operación. Contrato en specs/openapi.yaml
# (MargenLineasWeb, ConexoLineasWeb, ServicioConexoCosteo.servicio_id). Por HTTP contra la API real en memoria, SQL Server 2019 en
# Testcontainers y el Nexo falso; las mutaciones envían If-Match con la versión vigente.
# Pasos: tests/Cotifly.Aceptacion.Tests/Costeo/clsPasosCosteoLineas.cs; los de la licitación y del formulario se reutilizan.
@integracion
Característica: Margen y servicios conexos para todas las rutas en una operación
  Como comercial de Botero Soto
  Quiero aplicar el margen o un servicio conexo a todas las rutas de una vez
  Para no repetir el mismo cambio ruta por ruta ni dejar la cotización a medias

  Antecedentes:
    Dado la API web de Cotifly conectada a Nexo con el cliente "900555111" de BP "15927"
    Y el comercial "ana" con sesión en la regional "Occidente"

  Escenario: El mismo margen a todas las rutas en una llamada, con una sola auditoría
    Cuando "ana" crea la licitación "L1" del cliente "900555111" con el pliego de 3 filas mapeadas
    Y Nexo costea las líneas 1 y 2 de "L1" con un flete de 1000000
    Y Nexo costea las líneas 3 de "L1" con un flete de 1000000
    Y el polling consulta a Nexo por "L1"
    Y "ana" aplica el margen "0.20" a todas las rutas de la licitación "L1"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CosteoCotizacion" del contrato
    Y cada ruta de "L1" tiene el margen "0.20" con el origen "negociado"
    Y quedó una sola auditoría "ajustar_margen_costeo_lineas" de "L1"

  Escenario: Volver al margen sugerido en todas las rutas
    Cuando "ana" crea la licitación "L1" del cliente "900555111" con el pliego de 3 filas mapeadas
    Y Nexo costea las líneas 1 y 2 de "L1" con un flete de 1000000
    Y Nexo costea las líneas 3 de "L1" con un flete de 1000000
    Y el polling consulta a Nexo por "L1"
    Y "ana" ajusta el margen de la ruta 2 de la licitación "L1" a "0.40"
    Y "ana" vuelve al margen sugerido todas las rutas de la licitación "L1"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CosteoCotizacion" del contrato
    Y ninguna ruta de "L1" tiene margen negociado

  Escenario: Una ruta sin precio evita el cambio en las demás y el error dice cuál
    Cuando "ana" crea la licitación "L1" del cliente "900555111" con el pliego de 3 filas mapeadas
    Y Nexo costea las líneas 1 y 2 de "L1" con un flete de 1000000
    Y el polling consulta a Nexo por "L1"
    Y "ana" aplica el margen "0.20" a todas las rutas de la licitación "L1"
    Entonces la respuesta tiene el estado 409 con el código de negocio "COSTEO_NO_LISTO"
    Y el error señala la ruta 3
    Y ninguna ruta de "L1" tiene margen negociado
    Y no hay ajustes de margen de la licitación "L1"

  Escenario: Un servicio conexo agregado a todas las rutas se identifica por su servicio_id
    Dado el catálogo de Nexo tiene estos servicios conexos:
      | id                                   | codigo | nombre         | categoria | unidad | costo_unitario | activo |
      | 6f1c2a9e-3b7d-4c55-9a0e-1d2f3a4b5c6d | ALM-01 | Almacenamiento | logistico | Día    | 120000         | true   |
    Y "ana" envió a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo costeó la solicitud de "C1" con un flete de 2550000 y el polling lo registró
    Cuando "ana" agrega el servicio conexo "6f1c2a9e-3b7d-4c55-9a0e-1d2f3a4b5c6d" con la cantidad 1 a todas las rutas de "C1"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CosteoCotizacion" del contrato
    Y cada ruta de "C1" tiene el conexo "Almacenamiento" con el servicio_id "6f1c2a9e-3b7d-4c55-9a0e-1d2f3a4b5c6d" pendiente de enviar a Nexo
    Y quedó una sola auditoría "agregar_servicio_conexo_lineas" de "C1"
