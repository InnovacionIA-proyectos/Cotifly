# language: es
# HU-005.13 (#43) — Resolver en el chat el origen y el destino escritos en texto libre contra el catálogo de municipios de Nexo.
# Cambio de alcance: la issue hablaba de GET /departamentos y GET /ciudades?departamento=&q= (cascada), pero el mapeo aprobado
# (docs/MAPEO_CAMPOS_NEXO_COTIFLY.md) dejó un solo endpoint, GET /api/captura-solicitudes/municipios, sin filtro: la búsqueda se
# hace en Cotifly (clsResolvedorMunicipios) con la herramienta resolver_municipio. Una coincidencia se usa; varias (mismo municipio
# en más de un departamento) se preguntan antes de cotizar, igual que los clientes candidatos; sin catálogo se sigue con el texto.
# Por HTTP contra la API real con SQL Server 2019 en Testcontainers, el modelo guionizado y el catálogo de Nexo falso (sin red).
@integracion
Característica: Origen y destino del chat resueltos contra el catálogo de municipios de Nexo
  Como comercial de Botero Soto
  Quiero que el agente reconozca la ciudad que escribo y me pregunte cuando hay varias con el mismo nombre
  Para que la cotización nunca salga con un municipio que el agente supuso

  Antecedentes:
    Dado la API web de Cotifly con el agente guionizado
    Y el catálogo de municipios de Nexo tiene:
      | municipio    | departamento    |
      | La Unión     | Antioquia       |
      | La Unión     | Nariño          |
      | La Unión     | Valle del Cauca |
      | Yumbo        | Valle del Cauca |
      | Buenaventura | Valle del Cauca |
      | Bogotá       | Cundinamarca    |
    Y el comercial "ana" con sesión en la regional "Occidente"

  Escenario: Un municipio con un solo resultado se usa directo con su departamento
    Dado que en el turno 1 el modelo resuelve el origen "Yumbo" y busca la tarifa "Nacional" de "Yumbo" a "Buenaventura"
    Cuando "ana" escribe en el chat "Cotiza Nacional de Yumbo a Buenaventura"
    Entonces la herramienta "resolver_municipio" respondió al modelo el municipio "Yumbo" de "Valle del Cauca"
    Y la tarifa se buscó con el origen "Yumbo, Valle del Cauca"
    Y la sesión de "ana" guardó el origen "Yumbo, Valle del Cauca" verificado en el catálogo

  Escenario: Un municipio que existe en varios departamentos se pregunta antes de cotizar
    Dado que en el turno 1 el modelo resuelve el origen "La Unión" y busca la tarifa "Nacional" de "La Unión" a "Bogotá"
    Y que en el turno 2 el modelo busca la tarifa "Nacional" de "La Unión" a "Bogotá"
    Cuando "ana" escribe en el chat "Cotiza Nacional de La Unión a Bogotá"
    Entonces la herramienta "resolver_municipio" respondió al modelo los candidatos de "Antioquia, Nariño, Valle del Cauca"
    Y la herramienta "buscar_tarifa" respondió al modelo el código "CONFIRMACION_REQUERIDA"
    Y no se buscó ninguna tarifa
    Y la sesión de "ana" tiene 3 municipios candidatos para el origen
    Cuando "ana" escribe en el chat "La de Nariño"
    Entonces la tarifa se buscó con el origen "La Unión, Nariño"
    Y la sesión de "ana" guardó el origen "La Unión, Nariño" verificado en el catálogo
    Y la sesión de "ana" tiene 0 municipios candidatos para el origen

  Escenario: Sin el catálogo de municipios el agente sigue con el texto del comercial
    Dado que el catálogo de municipios no está disponible
    Y que en el turno 1 el modelo resuelve el origen "La Unión" y busca la tarifa "Nacional" de "La Unión" a "Bogotá"
    Cuando "ana" escribe en el chat "Cotiza Nacional de La Unión a Bogotá"
    Entonces la herramienta "resolver_municipio" respondió al modelo el código "MUNICIPIOS_NO_DISPONIBLE"
    Y la tarifa se buscó con el origen "La Unión"
