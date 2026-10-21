# language: es
# HU-032 (#197, ADR-022) — Vehículo elegido como respaldo del cubicador. El comercial vuelve a elegir el vehículo de cada ruta. Una ruta con
# todas sus mercancías cubicables va al cubicador: si asigna, manda el cubicador (y se avisa si el elegido era otro); si no selecciona o se
# cae, va el elegido validado contra Nexo en ese momento. Una ruta sin mercancías cubicables (solo peso, mercancías sin medidas, o mixta) no
# va al cubicador y usa el elegido, obligatorio salvo en Contenedor. Las mercancías sin medidas se guardan y no viajan a Nexo como
# posiciones. Por HTTP contra la API real en memoria, SQL Server 2019 en Testcontainers, un Nexo falso y un cubicador falso.
# Pasos: tests/Cotifly.Aceptacion.Tests/Costeo/clsPasosVehiculoElegido.cs.
@integracion
Característica: Vehículo elegido o cubicador
  Como comercial de Botero Soto
  Quiero elegir el vehículo de cada ruta y que el cubicador solo lo cambie cuando pueda cubicar la carga, avisándome
  Para cotizar rutas sin medidas y no perder el vehículo que escogí

  Antecedentes:
    Dado que el cubicador tiene el tipo de mercancía "Café en estibas" de las mercancías del formulario
    Y la API web de Cotifly conectada a Nexo con el cliente "900555111" de BP "15927" y al cubicador
    Y el comercial "ana" con sesión en la regional "Occidente"

  Escenario: Una ruta solo con peso no va al cubicador y lleva el vehículo elegido
    Cuando "ana" envía a costeo la ruta "V1" de "Cupo" con el vehículo "2s3" y 9000 kg sin mercancías
    Entonces la respuesta tiene el estado 201 y cumple el esquema "CotizacionResumen" del contrato
    Y el cubicador no recibió ninguna recomendación
    Y la ruta 1 de "V1" tiene un camión con el vehículo "2S3" y guarda el vehículo elegido "2S3"
    Y Nexo recibió una línea con el vehículo "2S3", el peso 9000 y sin posiciones

  Escenario: Sin mercancías cubicables el vehículo es obligatorio salvo en Contenedor
    Cuando "ana" envía a costeo la ruta "V1" de "Cupo" sin vehículo y 9000 kg sin mercancías
    Entonces la respuesta tiene el estado 400 con el código de negocio "SOLICITUD_COSTEO_INVALIDA"
    Y el error trae exactamente estos errores por ruta:
      | ruta | campo         | codigo             |
      | 0    | tipo_vehiculo | VEHICULO_REQUERIDO |
    Cuando "ana" envía a costeo la ruta "V2" de "Contenedor" sin vehículo y 9000 kg sin mercancías
    Entonces la respuesta tiene el estado 201 y cumple el esquema "CotizacionResumen" del contrato

  Escenario: Las mercancías sin medidas se aceptan, se guardan y no viajan a Nexo como posiciones
    Cuando "ana" envía a costeo la ruta "V1" de "Cupo" con el vehículo "3S3" y estas mercancías:
      | nombre_mercancia | cantidad | peso_kg |
      | Bultos de papa   | 10       | 500     |
    Entonces la respuesta tiene el estado 201 y cumple el esquema "CotizacionResumen" del contrato
    Y el cubicador no recibió ninguna recomendación
    Y la ruta 1 de "V1" guarda 1 mercancía sin medidas
    Y Nexo recibió una línea con el vehículo "3S3", el peso 5000 y sin posiciones

  Escenario: Una ruta mixta no se cubica, lleva el vehículo elegido y lo avisa
    Cuando "ana" envía a costeo la ruta "V1" de "Cupo" con el vehículo "3S3" y estas mercancías:
      | nombre_mercancia | cantidad | peso_kg | codigo_mercancia | largo_cm | ancho_cm | alto_cm |
      | Café             | 1        | 1000    | CAFE_EN_ESTIBAS  | 120      | 100      | 150     |
      | Bultos de papa   | 10       | 500     |                  |          |          |         |
    Entonces la respuesta tiene el estado 201 y cumple el esquema "CotizacionResumen" del contrato
    Y el cubicador no recibió ninguna recomendación
    Y la respuesta avisa "MERCANCIAS_NO_CUBICABLES" en la ruta 1

  Escenario: Medidas parciales son un dato mal digitado
    Cuando "ana" envía a costeo la ruta "V1" de "Cupo" con el vehículo "3S3" y estas mercancías:
      | nombre_mercancia | cantidad | peso_kg | largo_cm |
      | Caja             | 1        | 500     | 120      |
    Entonces la respuesta tiene el estado 400 con el código de negocio "SOLICITUD_COSTEO_INVALIDA"
    Y el error trae exactamente estos errores por ruta:
      | ruta | campo                 | codigo            |
      | 0    | mercancias[0].ancho_cm | POSICION_INVALIDA |

  Escenario: Si el cubicador asigna otro vehículo manda el cubicador y avisa el ajuste
    Dado que el cubicador asigna para cada ruta estos vehículos:
      | vehiculo | carroceria | peso_kg |
      | 3S3      | FURGON     | 1000    |
    Cuando "ana" envía a costeo la ruta "V1" de "Cupo" con el vehículo "2S3" y una mercancía cubicable de 1000 kg
    Entonces la respuesta tiene el estado 201 y cumple el esquema "CotizacionResumen" del contrato
    Y la ruta 1 de "V1" tiene un camión con el vehículo "3S3" y guarda el vehículo elegido "2S3"
    Y la respuesta avisa "VEHICULO_AJUSTADO_POR_CUBICADOR" en la ruta 1

  Escenario: Si el cubicador no selecciona vehículo va el elegido y se avisa
    Dado que el cubicador no puede escoger vehículo por "SIN_CARROCERIA_COMPATIBLE"
    Cuando "ana" envía a costeo la ruta "V1" de "Cupo" con el vehículo "2s3" y una mercancía cubicable de 1000 kg
    Entonces la respuesta tiene el estado 201 y cumple el esquema "CotizacionResumen" del contrato
    Y la ruta 1 de "V1" tiene un camión con el vehículo "2S3" y guarda el vehículo elegido "2s3"
    Y la respuesta avisa "VEHICULO_ELEGIDO_USADO" en la ruta 1

  Escenario: Si el cubicador no selecciona y el elegido no es de Nexo la ruta va sin vehículo y la captura no se pierde
    Dado que el cubicador no puede escoger vehículo por "SIN_CARROCERIA_COMPATIBLE"
    Cuando "ana" envía a costeo la ruta "V1" de "Cupo" con el vehículo "TRACTOMULA" y una mercancía cubicable de 1000 kg
    Entonces la respuesta tiene el estado 201 y cumple el esquema "CotizacionResumen" del contrato
    Y la ruta 1 de "V1" tiene un camión sin vehículo y guarda el vehículo elegido "TRACTOMULA"
    Y la respuesta avisa "VEHICULO_ELEGIDO_INVALIDO" en la ruta 1

  Escenario: Si el cubicador se cae va el vehículo elegido
    Dado que el cubicador no responde
    Cuando "ana" envía a costeo la ruta "V1" de "Cupo" con el vehículo "3S3" y una mercancía cubicable de 1000 kg
    Entonces la respuesta tiene el estado 201 y cumple el esquema "CotizacionResumen" del contrato
    Y la ruta 1 de "V1" tiene un camión con el vehículo "3S3" y guarda el vehículo elegido "3S3"
    Y la respuesta avisa "VEHICULO_ELEGIDO_USADO" en la ruta 1

  Escenario: Un vehículo que no es de Nexo en una ruta sin mercancías cubicables se rechaza antes de guardar
    Cuando "ana" envía a costeo la ruta "V1" de "Cupo" con el vehículo "TRACTOMULA" y 9000 kg sin mercancías
    Entonces la respuesta tiene el estado 400 con el código de negocio "SOLICITUD_COSTEO_INVALIDA"
    Y el error trae exactamente estos errores por ruta:
      | ruta | campo         | codigo             |
      | 0    | tipo_vehiculo | CODIGO_NO_CATALOGO |

  Escenario: Editar solo el vehículo de una ruta sin mercancías cubicables es un cambio que viaja a Nexo
    Dado "ana" envió a costeo la ruta "V1" de "Cupo" con el vehículo "2S3" y 9000 kg sin mercancías y Nexo no la recibió
    Cuando "ana" edita la solicitud "V1" cambiando solo el vehículo a "3S3"
    Entonces la edición de "V1" no es "sin cambios" y replanificó la ruta 1
    Y la ruta 1 de "V1" tiene un camión con el vehículo "3S3" y guarda el vehículo elegido "3S3"
    Y Nexo recibió una línea con el vehículo "3S3", el peso 9000 y sin posiciones
