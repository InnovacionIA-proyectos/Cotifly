# language: es
# HU-021.9 (#136) — Decisión 10 del usuario: los servicios conexos capturados con la ruta llegan a la cotización de Nexo con un envío
# diferido. El POST de conexos de Nexo exige la cotización y la línea del camión, que solo existen cuando Nexo costea: el polling los
# envía UNA sola vez a la línea del primer camión vigente de la ruta. El precio en Cotifly sigue siendo el costo del catálogo de Nexo.
# Por HTTP contra la API real en memoria, SQL Server en Testcontainers y un Nexo falso.
@integracion
Característica: Envío diferido de los servicios conexos a Nexo
  Como comercial de Botero Soto
  Quiero que los servicios conexos que capturo con la ruta lleguen a la cotización de Nexo
  Para que la cotización de Nexo y la de Cotifly coincidan sin tener que agregarlos dos veces

  Antecedentes:
    Dado la API web de Cotifly conectada a Nexo con el cliente "900555111" de BP "15927"
    Y el comercial "ana" con sesión en la regional "Occidente"
    Y que Nexo tiene en su catálogo el servicio conexo "Cargue en puerto" con un costo de 150000

  Escenario: Los conexos capturados llegan a Nexo una sola vez después de la entrega de la Mesa
    Dado "ana" envió a costeo una ruta del cliente "900555111" con 2 del servicio conexo "Cargue en puerto" como "C1"
    Cuando la Mesa de Costeo de Nexo costea la solicitud de "C1" con un flete de 1000000 y la entrega
    Y el polling consulta a Nexo
    Y el polling consulta a Nexo
    Entonces Nexo recibió una sola vez 2 del servicio conexo "Cargue en puerto" de "C1" en la línea de su primer camión
    Y el servicio conexo "Cargue en puerto" de "C1" conserva el costo 150000 del catálogo
