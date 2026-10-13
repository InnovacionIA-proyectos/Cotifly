# language: es
# HU-023.2 (#157, ADR-020) — Transición previa al corte del cubicador: "Calcular cubicaje" (POST /web/cubicacion) deja de pasar por
# POST /api/cubicacion de Nexo y llama a POST /recomendaciones del cubicador con idLineaSolicitud = "PRUEBA-<guid>". Es una vista
# previa: Cotifly no guarda nada. La respuesta es una lista de vehículos (flota mixta) con el resultado, el motivo, la condicionada, las
# advertencias y la extradimensión. Sin código de mercancía, la descripción se resuelve al tipo más parecido del catálogo. Sin respaldo:
# con el cubicador caído o sin configurar responde 503 CUBICADOR_NO_DISPONIBLE. Por HTTP contra la API real en memoria, SQL Server en
# Testcontainers y el cubicador falso (nunca el real). Pasos en Cotifly.Aceptacion.Tests/Costeo/clsPasosCubicador.cs.
@integracion
Característica: Calcular cubicaje contra el cubicador
  Como comercial de Botero Soto
  Quiero ver qué vehículos recomienda el cubicador para la carga antes de enviar la solicitud
  Para saber cuántos camiones y de qué tipo necesita la ruta

  Antecedentes:
    Dado que el cubicador tiene el tipo de mercancía "Café en estibas" de las mercancías del formulario
    Y la API web de Cotifly conectada a Nexo con el cliente "900555111" de BP "15927" y al cubicador
    Y el comercial "ana" con sesión en la regional "Occidente"

  Escenario: El día del corte el comercial ve la recomendación nueva con flota mixta y no se guarda nada
    Dado que el cubicador asigna para cada ruta estos vehículos:
      | vehiculo | carroceria | peso_kg |
      | 3S3      | FURGON     | 16000   |
      | C2       | ESTACAS    | 4500    |
    Cuando "ana" calcula el cubicaje con el código de mercancía "CAFE_EN_ESTIBAS"
    Entonces el cubicaje trae estos vehículos:
      | vehiculo | nombre           | carroceria | peso_kg |
      | 3S3      | Tractocamión 3S3 | FURGON     | 16000   |
      | C2       | Sencillo C2      | ESTACAS    | 4500    |
    Y el cubicador recibió una corrida de prueba con el código de mercancía "CAFE_EN_ESTIBAS"
    Y "ana" no tiene cotizaciones guardadas

  Escenario: El cubicador no puede escoger camión
    Dado que el cubicador no puede escoger vehículo por "EXTRADIMENSIONADO"
    Cuando "ana" calcula el cubicaje con el código de mercancía "CAFE_EN_ESTIBAS"
    Entonces el cubicaje trae el resultado "SIN_SELECCION_AUTOMATICA" con el motivo "EXTRADIMENSIONADO" y sin vehículos

  Escenario: Sin desplegable la descripción se resuelve a la mercancía más parecida del catálogo
    Cuando "ana" calcula el cubicaje con la descripción "CAFÉ"
    Entonces el cubicador recibió una corrida de prueba con el código de mercancía "CAFE_EN_ESTIBAS"

  Escenario: Una mercancía que no se parece a ninguna del catálogo se rechaza con un mensaje claro
    Cuando "ana" calcula el cubicaje con la descripción "Electrodomésticos"
    Entonces la respuesta tiene el estado 400 con el código de negocio "MERCANCIA_DESCONOCIDA"

  Escenario: Con el cubicador caído no hay respaldo
    Dado que el cubicador no responde
    Cuando "ana" calcula el cubicaje con el código de mercancía "CAFE_EN_ESTIBAS"
    Entonces la respuesta tiene el estado 503 con el código de negocio "CUBICADOR_NO_DISPONIBLE"
    Y "ana" no tiene cotizaciones guardadas
