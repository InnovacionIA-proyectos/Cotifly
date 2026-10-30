# language: es
# HU-009 (#139, B1) — Un margen de SAP TM fuera de [0, 60 %] ("anómalo", tras redondearlo a 4 decimales como la columna) no se aplica a
# ciegas: la ruta usa el margen de la política de su línea, el de SAP queda como marca y GET .../costeo avisa al comercial
# (`margen_sap_anomalo`, `aviso_margen` en RutaCosteo de specs/openapi.yaml). Por HTTP contra la API real en memoria, SQL Server 2019 en
# Testcontainers, el Nexo falso y una fuente SAP TM fija. Política de la línea: 15 %, método "margen".
# Pasos: tests/Cotifly.Aceptacion.Tests/Costeo/clsPasosMargenSapAnomalo.cs y los de clsPasosAjusteMargen.cs.
@integracion
Característica: Margen anómalo de SAP TM en el costeo
  Como comercial de Botero Soto
  Quiero que un margen de SAP TM fuera de lo normal no se aplique solo y me avise
  Para revisarlo antes de cotizar y decidir yo el margen de la ruta

  Escenario: Un margen de SAP TM de 75 % no se aplica, se avisa y el comercial ajusta la ruta
    Dado la API web de Cotifly conectada a Nexo con el cliente "900555111" de BP "15927" y SAP TM que propone "75"
    Y el comercial "ana" con sesión en la regional "Occidente"
    Y "ana" envió a costeo el formulario ampliado del cliente "900555111" como "C1"
    Y Nexo costeó la solicitud de "C1" con un flete de 2550000 y el polling lo registró
    Entonces el costeo de "C1" tiene el precio final 3000000 con el margen aplicado "0.15" y el origen "politica_linea"
    Y "ana" ve en la ruta 1 de "C1" el aviso de margen SAP anómalo de "75 %"
    Y el margen sugerido de "C1" sigue en "0.15"
    Cuando "ana" ajusta el margen de "C1" a "0.18" sin justificación
    Entonces la respuesta tiene el estado 200 y cumple el esquema "CosteoCotizacion" del contrato
    Y el costeo de "C1" tiene el precio final 3109756 con el margen aplicado "0.18" y el origen "negociado"
    Y queda un ajuste de margen de "C1" de "0.15" a "0.18" por "ana" sin justificación
