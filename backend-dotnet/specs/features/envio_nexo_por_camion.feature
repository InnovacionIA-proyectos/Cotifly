# language: es
# HU-021.7 (#134, ADR-019) — Envío a Nexo por camión, polling y emparejamiento: una ruta viaja a Nexo como una línea por camión
# (con los datos de su ruta y de su camión), Cotifly empareja las líneas que devuelve Nexo (en cualquier orden) con sus camiones por
# contenido, resuelve los empates de camiones idénticos en orden estable, copia el vehículo que pone la Mesa, ignora los camiones
# descartados y pega los conexos de la ruta a la línea de su primer camión. Mientras el cubicador (#133) no cree los camiones, los
# escenarios agregan los camiones extra directamente en la base antes de reintentar el envío.
# Por HTTP contra la API real en memoria, SQL Server 2019 en Testcontainers y el Nexo falso (nunca el real).
# Pasos en Cotifly.Aceptacion.Tests/Costeo/clsPasosEnvioPorCamion.cs.
@integracion
Característica: Envío a Nexo por camión y emparejamiento de sus líneas
  Como comercial de Botero Soto
  Quiero que cada camión de una ruta se costee en Nexo como una línea propia
  Para cotizar la ruta con el costo real de todos sus camiones aunque Nexo devuelva las líneas en cualquier orden

  Antecedentes:
    Dado la API web de Cotifly conectada a Nexo con el cliente "900555111" de BP "15927"
    Y el comercial "ana" con sesión en la regional "Occidente"
    Y que Nexo responde 503 al alta de solicitudes

  Escenario: Una ruta con tres camiones viaja como tres líneas y cada camión recibe el flete de su línea
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y la ruta 1 de "C1" lleva además estos camiones:
      | vehiculo | carroceria | peso_kg |
      | 2S2      | FURGON     | 8000    |
      | 2        |            | 4000    |
    Y Nexo vuelve a responder
    Y "ana" reintenta el envío a costeo de "C1"
    Entonces Nexo recibió 3 líneas de "Buenaventura" a "Bogotá D.C.", una por camión y sin posiciones
    Y las líneas que recibió Nexo llevan los pesos "28500, 8000, 4000"
    Y la nota que recibió Nexo menciona "Ruta 1 (Buenaventura → Bogotá D.C.): 3 camiones."
    Cuando Nexo devuelve las líneas de la solicitud en orden aleatorio
    Y Nexo costea la línea del camión de 28500 kg de "C1" con un flete de 1275000
    Y Nexo costea la línea del camión de 8000 kg de "C1" con un flete de 850000
    Y Nexo costea la línea del camión de 4000 kg de "C1" con un flete de 425000
    Y el polling consulta a Nexo
    Entonces cada camión de "C1" quedó asociado a una línea distinta de Nexo
    Y el camión de 28500 kg de "C1" tiene el flete 1275000 y el estado "autocosteo"
    Y el camión de 8000 kg de "C1" tiene el flete 850000 y el estado "autocosteo"
    Y el camión de 4000 kg de "C1" tiene el flete 425000 y el estado "autocosteo"
    Y "Mis cotizaciones" de "ana" muestra "C1" en "autocosteo" con el precio final 3000000

  Escenario: Dos camiones idénticos de la misma ruta se emparejan en orden estable y la ruta espera a los dos
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y la ruta 1 de "C1" lleva además estos camiones:
      | vehiculo | carroceria        | peso_kg |
      | 3S3      | PORTACONTENEDORES | 28500   |
    Y Nexo vuelve a responder
    Y "ana" reintenta el envío a costeo de "C1"
    Y Nexo devuelve las líneas de la solicitud en orden aleatorio
    Y Nexo costea la línea 1 de la solicitud de "C1" con un flete de 1275000
    Y el polling consulta a Nexo
    Entonces cada camión de "C1" quedó asociado a una línea distinta de Nexo
    Y la cotización "C1" queda en el estado de costeo "pendiente_costeo"
    Y "Mis cotizaciones" de "ana" muestra "C1" sin precio final
    Cuando Nexo costea la línea 2 de la solicitud de "C1" con un flete de 1275000
    Y el polling consulta a Nexo
    Entonces cada camión de "C1" quedó asociado a una línea distinta de Nexo
    Y "Mis cotizaciones" de "ana" muestra "C1" en "autocosteo" con el precio final 3000000

  Escenario: La Mesa de Costeo le pone vehículo a la línea que llegó vacía y Cotifly lo copia al camión
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y la ruta 1 de "C1" lleva además estos camiones:
      | vehiculo | carroceria | peso_kg |
      |          |            | 4000    |
    Y Nexo vuelve a responder
    Y "ana" reintenta el envío a costeo de "C1"
    Entonces una de las líneas que recibió Nexo no lleva vehículo ni carrocería
    Cuando la Mesa de Nexo le pone el vehículo "2S2" con la carrocería "ESTACAS" a la línea del camión de 4000 kg de "C1"
    Y Nexo costea la línea del camión de 28500 kg de "C1" con un flete de 2125000
    Y Nexo costea la línea del camión de 4000 kg de "C1" con un flete de 425000
    Y el polling consulta a Nexo
    Entonces el camión de 4000 kg de "C1" tiene el vehículo "2S2" y la carrocería "ESTACAS"
    Y el camión de 4000 kg de "C1" tiene el flete 425000 y el estado "autocosteo"
    Y "Mis cotizaciones" de "ana" muestra "C1" en "autocosteo" con el precio final 3000000

  Escenario: Un camión descartado no se lee ni frena el precio de su ruta
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y la ruta 1 de "C1" lleva además estos camiones:
      | vehiculo | carroceria | peso_kg |
      | 2S2      | FURGON     | 8000    |
      | 2        |            | 4000    |
    Y Nexo vuelve a responder
    Y "ana" reintenta el envío a costeo de "C1"
    Y el camión de 4000 kg de "C1" queda descartado
    Y Nexo costea la línea del camión de 28500 kg de "C1" con un flete de 1700000
    Y Nexo costea la línea del camión de 8000 kg de "C1" con un flete de 850000
    Y el polling consulta a Nexo
    Entonces el camión de 4000 kg de "C1" sigue descartado y sin línea de Nexo
    Y el camión de 8000 kg de "C1" tiene el flete 850000 y el estado "autocosteo"
    Y "Mis cotizaciones" de "ana" muestra "C1" en "autocosteo" con el precio final 3000000

  Escenario: Los conexos de la ruta viajan a Nexo pegados a la línea de su primer camión
    Dado el catálogo de Nexo tiene estos servicios conexos:
      | id                                   | codigo | nombre         | categoria | unidad | costo_unitario | activo |
      | 6f1c2a9e-3b7d-4c55-9a0e-1d2f3a4b5c6d | ALM-01 | Almacenamiento | logistico | Día    | 120000         | true   |
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y la ruta 1 de "C1" lleva además estos camiones:
      | vehiculo | carroceria | peso_kg |
      | 2S2      | FURGON     | 8000    |
      | 2        |            | 4000    |
    Y Nexo vuelve a responder
    Y "ana" reintenta el envío a costeo de "C1"
    Y Nexo devuelve las líneas de la solicitud en orden aleatorio
    Y Nexo costea la línea del camión de 28500 kg de "C1" con un flete de 1275000
    Y Nexo costea la línea del camión de 8000 kg de "C1" con un flete de 850000
    Y Nexo costea la línea del camión de 4000 kg de "C1" con un flete de 425000
    Y el polling consulta a Nexo
    Y "ana" agrega a "C1" en la línea 1 el servicio conexo "6f1c2a9e-3b7d-4c55-9a0e-1d2f3a4b5c6d" con la cantidad 2
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CosteoCotizacion" del contrato
    Y Nexo recibió el conexo de "C1" en la línea de la cotización de Nexo del primer camión de su ruta 1
    Y el servicio conexo "Almacenamiento" de "C1" está incluido con el precio 282353
