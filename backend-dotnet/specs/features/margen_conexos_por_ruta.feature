# language: es
# HU-021.8 (#135, ADR-019 decisiones 3, 4 y 6) — Margen y servicios conexos POR RUTA en la etapa final del costeo:
# PUT /web/cotizaciones/{folio}/costeo/margen y PUT /web/cotizaciones/{folio}/costeo/servicios-conexos con la ruta (linea_orden o
# linea_solicitud_id). Contrato en specs/openapi.yaml (AjusteMargenWeb, SeleccionServiciosConexos, CosteoCotizacion.rutas, RutaCosteo).
# Por HTTP contra la API real en memoria, SQL Server 2019 en Testcontainers y el Nexo falso. Las mutaciones envían If-Match con la
# versión vigente (ADR-018 §8). El asistente que pregunta la ruta se prueba en clsAsistenteMargenPorRutaTests (sin LLM real).
# Pasos: tests/Cotifly.Aceptacion.Tests/Costeo/clsPasosMargenConexosPorRuta.cs; los de la licitación y del formulario se reutilizan.
@integracion
Característica: Margen y servicios conexos por ruta
  Como comercial de Botero Soto
  Quiero ajustar el margen y los servicios conexos de cada ruta por separado
  Para negociar cada trayecto sin tocar el precio de los demás

  Antecedentes:
    Dado la API web de Cotifly conectada a Nexo con el cliente "900555111" de BP "15927"
    Y el comercial "ana" con sesión en la regional "Occidente"

  Escenario: Con una sola ruta el ajuste no necesita indicarla y queda registrado con esa ruta
    Dado "ana" envió a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo costeó la solicitud de "C1" con un flete de 2550000 y el polling lo registró
    Cuando "ana" ajusta el margen de "C1" a "0.18" sin justificación
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CosteoCotizacion" del contrato
    Y el ajuste de margen de "C1" quedó registrado en la ruta 1

  Escenario: Con tres rutas el ajuste sin indicar la ruta pide cuál y no cambia nada
    Cuando "ana" crea la licitación "L1" del cliente "900555111" con el pliego de 3 filas mapeadas
    Y Nexo costea las líneas 1 y 2 de "L1" con un flete de 1000000
    Y Nexo costea las líneas 3 de "L1" con un flete de 1000000
    Y el polling consulta a Nexo por "L1"
    Y "ana" ajusta el margen de la licitación "L1" a "0.20" sin indicar la ruta
    Entonces la respuesta tiene el estado 400 con el código de negocio "RUTA_REQUERIDA"
    Y el detalle del error lista las 3 rutas de "L1" sin montos
    Y ninguna ruta de "L1" tiene margen negociado
    Y no hay ajustes de margen de la licitación "L1"

  Escenario: Con tres rutas el ajuste de la ruta 2 solo cambia esa ruta y el total
    Cuando "ana" crea la licitación "L1" del cliente "900555111" con el pliego de 3 filas mapeadas
    Y Nexo costea las líneas 1 y 2 de "L1" con un flete de 1000000
    Y Nexo costea las líneas 3 de "L1" con un flete de 1000000
    Y el polling consulta a Nexo por "L1"
    Y "ana" ajusta el margen de la ruta 2 de la licitación "L1" a "0.40"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CosteoCotizacion" del contrato
    Y solo la ruta 2 de "L1" tiene el margen "0.40" con el origen "negociado"
    Y el precio de la ruta 2 de "L1" es 1666667 y el total es la suma de sus rutas
    Y el ajuste de margen de "L1" quedó registrado en la ruta 2

  Escenario: Una ruta que no es de la cotización se rechaza
    Cuando "ana" crea la licitación "L1" del cliente "900555111" con el pliego de 3 filas mapeadas
    Y Nexo costea las líneas 1 y 2 de "L1" con un flete de 1000000
    Y Nexo costea las líneas 3 de "L1" con un flete de 1000000
    Y el polling consulta a Nexo por "L1"
    Y "ana" ajusta el margen de la ruta 7 de la licitación "L1" a "0.20"
    Entonces la respuesta tiene el estado 400 con el código de negocio "RUTA_INEXISTENTE"

  Escenario: Cambiar la cantidad de un conexo de la ruta y después quitarlo, con auditoría
    Dado "ana" envió a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo costeó la solicitud de "C1" con un flete de 2550000 y el polling lo registró
    Y que el costeo de "C1" trae el servicio conexo "Cargue" con un valor de Nexo de 85000
    Cuando "ana" deja el servicio conexo "Cargue" en la ruta 1 de "C1" con la cantidad 2
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CosteoCotizacion" del contrato
    Y el costeo de "C1" tiene el precio final 3200000 con el margen aplicado "0.15" y el origen "politica_linea"
    Cuando "ana" quita el servicio conexo "Cargue" de la ruta 1 de "C1"
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CosteoCotizacion" del contrato
    Y el costeo de "C1" tiene el precio final 3000000 con el margen aplicado "0.15" y el origen "politica_linea"
    Y la ruta 1 de "C1" no tiene servicios conexos guardados
    Y quedó auditado que se quitó el servicio conexo "Cargue" de "C1"

  # HU-033 (#199): el PDF del costeo no desglosa. Cada ruta muestra su precio final (transporte + conexos, con el margen) y los conexos
  # incluidos solo por nombre; el total va como "VALOR TOTAL DE LA COTIZACIÓN". (2.550.000 + 170.000) / (1 − 0,15) = 3.200.000.
  Escenario: El PDF muestra el precio final de la ruta y el nombre del conexo sin el precio del conexo ni el transporte solo
    Dado "ana" envió a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo costeó la solicitud de "C1" con un flete de 2550000 y el polling lo registró
    Y que el costeo de "C1" trae el servicio conexo "Cargue en puerto" con un valor de Nexo de 170000
    Cuando "ana" genera el PDF del costeo de "C1"
    Entonces la respuesta tiene el estado 201 y cumple el esquema "SalidaPdf" del contrato
    Y el PDF de "C1" contiene "$3.200.000"
    Y el PDF de "C1" contiene "Cargue en puerto"
    Y el PDF de "C1" contiene "VALOR TOTAL DE LA COTIZACIÓN"
    Y el PDF de "C1" no contiene "$200.000"
    Y el PDF de "C1" no contiene "$3.000.000"
    Y el PDF de "C1" no contiene "2.550.000"
