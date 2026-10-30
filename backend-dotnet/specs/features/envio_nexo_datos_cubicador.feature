# language: es
# HU-023.4 (#159, ADR-020) — Envío a Nexo por camión con los datos del cubicador: con Nexo:EnviarGrupoRutaId y
# Nexo:EnviarDatosCubicador encendidas, cada línea lleva grupoRutaId (la ruta) e idRecomendacionVehiculo (el camión del cubicador), Nexo
# los devuelve en el GET y el polling empareja cada línea con SU camión aunque los camiones sean idénticos y Nexo devuelva las líneas en
# cualquier orden. Los camiones del cubicador se simulan en la base (id de recomendación) antes de reintentar el envío.
# Por HTTP contra la API real en memoria, SQL Server 2019 en Testcontainers y el Nexo falso (nunca el real).
# Pasos en Cotifly.Aceptacion.Tests/Costeo/clsPasosEnvioPorCamion.cs y clsPasosCosteo.cs.
@integracion
Característica: Envío a Nexo con el grupo de ruta y el camión del cubicador
  Como comercial de Botero Soto
  Quiero que cada camión del cubicador reciba el costo de su propia línea de Nexo
  Para que el costo de la ruta no dependa del orden en que Nexo devuelve las líneas

  Antecedentes:
    Dado la API web de Cotifly conectada a Nexo con el grupo de ruta y los datos del cubicador y el cliente "900555111" de BP "15927"
    Y el comercial "ana" con sesión en la regional "Occidente"
    Y que Nexo responde 503 al alta de solicitudes

  Escenario: Dos camiones idénticos de una ruta se emparejan por grupo y recomendación y cada uno recibe el costo de su línea
    Cuando "ana" envía a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y la ruta 1 de "C1" lleva además estos camiones:
      | vehiculo | carroceria | peso_kg |
      |          |            | 28500   |
    Y los camiones de "C1" tienen su recomendación del cubicador
    Y Nexo vuelve a responder
    Y "ana" reintenta el envío a costeo de "C1"
    Entonces cada línea que recibió Nexo lleva el grupo de su ruta y la recomendación de su camión
    Cuando Nexo devuelve las líneas de la solicitud en orden aleatorio
    Y Nexo costea la línea 1 de la solicitud de "C1" con un flete de 1000000
    Y Nexo costea la línea 2 de la solicitud de "C1" con un flete de 1550000
    Y el polling consulta a Nexo
    Entonces cada camión de "C1" quedó asociado a una línea distinta de Nexo
    Y el camión de la línea 1 de la solicitud de "C1" tiene el flete 1000000 y el estado "autocosteo"
    Y el camión de la línea 2 de la solicitud de "C1" tiene el flete 1550000 y el estado "autocosteo"
    Y "Mis cotizaciones" de "ana" muestra "C1" en "autocosteo" con el precio final 3000000
