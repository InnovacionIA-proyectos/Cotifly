# language: es
# HU-023.5 (#160, ADR-020) — Licitación con el cubicador: la licitación se guarda sin esperar al cubicador, sus rutas se cubican en
# segundo plano (con un límite de llamadas en paralelo entre réplicas, P6) y la solicitud viaja a Nexo una sola vez, cuando la última
# ruta quedó cubicada. Por HTTP contra la API real en memoria, SQL Server en Testcontainers, el Nexo falso y el cubicador falso.
# Pasos en Cotifly.Aceptacion.Tests/Costeo/clsPasosLicitacionCubicador.cs.
@integracion
Característica: Licitación con cubicador en segundo plano
  Como comercial de Botero Soto
  Quiero subir una licitación de muchas rutas sin esperar a que el cubicador calcule cada una
  Para ver el avance y que la solicitud llegue completa a Nexo

  Antecedentes:
    Dado que el cubicador tiene el tipo de mercancía "Café en estibas" de las mercancías del formulario
    Y la API web de Cotifly conectada a Nexo con el cliente "900555111" de BP "15927" y al cubicador
    Y el comercial "ana" con sesión en la regional "Occidente"

  Escenario: Una licitación de cuatro rutas se cubica en segundo plano y sale a Nexo completa
    Cuando "ana" crea una licitación de 4 rutas con la mercancía "CAFE_EN_ESTIBAS" para el cliente "900555111"
    Entonces la licitación está "en_curso" con 4 rutas pendientes y no se envió a Nexo
    Cuando el procesador cubica la licitación
    Entonces la licitación está "completa" con 0 rutas pendientes
    Y la licitación se envió a Nexo una sola vez con 4 líneas
