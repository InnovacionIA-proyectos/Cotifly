# language: es
# HU-035.1 (#202, ADR-024) — Importar las mercancías de una ruta desde un Excel: Cotifly lee el archivo localmente (sin Nexo) y
# devuelve la grilla cruda (hojas, encabezados y filas) para que el frontend mapee las columnas a tipo, cantidad, largo, ancho, alto y
# peso. Por HTTP contra la API real en memoria, SQL Server 2019 en Testcontainers (sesiones), un Nexo falso que nunca debe recibir el
# archivo y el cubicador falso.
@integracion
Característica: Excel de mercancías de una ruta
  Como comercial de Botero Soto
  Quiero cargar las mercancías de una ruta desde el Excel del cliente
  Para no copiar a mano el tipo, la cantidad, las medidas y el peso de cada una

  Antecedentes:
    Dado la API web de Cotifly conectada a Nexo con el cliente "900555111" de BP "15927" y al cubicador
    Y que el cubicador tiene el tipo de mercancía "Café en estibas" con el código "CAFE_EN_ESTIBAS"
    Y el comercial "ana" con sesión en la regional "Occidente"

  Escenario: Cotifly lee el Excel de mercancías sin llamar a Nexo y sugiere el código del catálogo
    Cuando "ana" analiza el Excel de mercancías de 3 filas
    Entonces la respuesta tiene el estado 200 y cumple el esquema "AnalisisExcelMercancias" del contrato
    Y la hoja "Mercancías" trae los encabezados "Tipo de mercancía|Cantidad|Largo (m)|Ancho (m)|Alto (m)|Peso (kg)"
    Y la hoja "Mercancías" trae 3 filas desde la fila 2 de Excel
    Y la fila 0 de la hoja "Mercancías" es "Cafe en estiba|10|1.2|1|1.5|850.5"
    Y la fila 0 de la hoja "Mercancías" sugiere el código "CAFE_EN_ESTIBAS"
    Y Nexo no recibió el archivo

  Escenario: Con el cubicador caído el análisis sigue sin sugerencias
    Dado que el cubicador no responde
    Cuando "ana" analiza el Excel de mercancías de 3 filas
    Entonces la respuesta tiene el estado 200 y cumple el esquema "AnalisisExcelMercancias" del contrato
    Y el análisis no trae el catálogo de mercancías
    Y la hoja "Mercancías" trae 3 filas desde la fila 2 de Excel

  Escenario: Una fórmula llega con el valor que Excel guardó y una fecha con formato estable
    Cuando "ana" analiza un Excel de mercancías con una fórmula y una fecha
    Entonces la respuesta tiene el estado 200 y cumple el esquema "AnalisisExcelMercancias" del contrato
    Y la fila 0 de la hoja "Mercancías" es "Bobinas|2|3|2|1|2000|2026-10-20"

  Escenario: Una hoja de más de 1000 filas llega truncada y lo dice
    Cuando "ana" analiza el Excel de mercancías de 1005 filas
    Entonces la respuesta tiene el estado 200 y cumple el esquema "AnalisisExcelMercancias" del contrato
    Y la hoja "Mercancías" trae 1000 filas de un total de 1005 y marca las filas truncadas

  Esquema del escenario: Un archivo corrupto o malicioso es un 400 con código, nunca un 500
    Cuando "ana" analiza como Excel de mercancías <archivo>
    Entonces la respuesta tiene el estado 400 con el código de negocio "<codigo>"
    Y Nexo no recibió el archivo

    Ejemplos:
      | archivo                                  | codigo                   |
      | un archivo de texto renombrado a .xlsx   | EXCEL_INVALIDO           |
      | un zip que no es un libro de Excel       | EXCEL_INVALIDO           |
      | una bomba zip                            | ARCHIVO_DEMASIADO_GRANDE |
      | un libro pequeño con millones de celdas  | ARCHIVO_DEMASIADO_GRANDE |
      | un libro con una entidad externa (XXE)   | EXCEL_INVALIDO           |
      | un archivo .csv                          | FORMATO_NO_SOPORTADO     |
      | un libro sin datos                       | EXCEL_SIN_DATOS          |

  Escenario: Sin multipart el análisis responde 415
    Cuando "ana" envía JSON al análisis del Excel de mercancías
    Entonces la respuesta tiene el estado 415 con un ErrorWeb

  Escenario: Sin sesión el análisis responde 401
    Cuando se analiza el Excel de mercancías sin sesión
    Entonces la respuesta tiene el estado 401 con un ErrorWeb
