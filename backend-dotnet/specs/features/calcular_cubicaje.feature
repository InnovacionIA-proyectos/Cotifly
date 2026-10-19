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

  # Opción D (E2E 2026-10-16, D3): el motor del cubicador tarda 26-31 s con cargas grandes y bloquea su API. Si no responde dentro de
  # Cubicador:EsperaSincronaCubicacionSegundos (2 s en estas pruebas, 8 por defecto), POST /web/cubicacion responde 202 y el cálculo
  # sigue en segundo plano; el front consulta GET /web/cubicacion/{id} hasta que termina. Solo el comercial que la pidió la ve.
  Escenario: Con el cubicador lento la UI recibe 202 y consulta la cubicación hasta que termina
    Dado que el cubicador asigna para cada ruta estos vehículos:
      | vehiculo | carroceria | peso_kg |
      | 3S3      | FURGON     | 16000   |
    Y que el cubicador se demora calculando la carga
    Y el comercial "luis" con sesión en la regional "Occidente"
    Cuando "ana" calcula el cubicaje con el código de mercancía "CAFE_EN_ESTIBAS"
    Entonces la respuesta tiene el estado 202
    Y la cubicación de "ana" está "en_proceso"
    Y "luis" no puede ver la cubicación de "ana"
    Cuando el cubicador termina de calcular
    Entonces la cubicación de "ana" termina "completada" con el vehículo "3S3"
