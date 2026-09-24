# language: es
# HU-003.4 (#10) — Cálculo de precio por margen o markup (ADR-010: paridad con mcp_server/precio.py).
# Casos de tests/test_precio.py del Python. Los importes van en pesos enteros; los márgenes, como fracción
# decimal entre comillas (0.135 = 13,5 %) para no depender de la cultura del .feature.
# Paridad con Python salvo empates exactos de medio peso: .NET redondea el valor exacto (half-to-even) y Python
# arrastra el error de float; diferencia máxima 1 peso (~0,04 % de casos). Ver ADR-010 §6.
# Se ejecuta en Cotifly.Aceptacion.Tests con el registro real de dependencias (AddAplicacion).
@precio
Característica: Cálculo de precio por margen o markup
  Como comercial de Botero Soto
  Quiero que el precio al cliente lo calcule el backend de forma determinista
  Para que el LLM nunca haga la aritmética y el resultado sea igual al del Python salvo empates exactos de medio peso

  Escenario: Valores exactos de la arquitectura
    Cuando calculo el precio de un costo de 2850000 con margen "0.135" por el método "margen"
    Entonces el precio es 3294798 y la utilidad 444798

  Esquema del escenario: Margen sobre el precio frente a recargo sobre el costo
    Cuando calculo el precio de un costo de <costo> con margen "<margen>" por el método "<metodo>"
    Entonces el precio es <precio> y la utilidad <utilidad>

    Ejemplos:
      | costo   | margen | metodo | precio  | utilidad |
      | 2850000 | 0.135  | markup | 3234750 | 384750   |
      | 1000000 | 0      | margen | 1000000 | 0        |
      | 1000000 | 1.5    | markup | 2500000 | 1500000  |

  Esquema del escenario: El redondeo a pesos es half-to-even sobre el valor exacto
    Cuando calculo el precio de un costo de <costo> con margen "<margen>" por el método "<metodo>"
    Entonces el precio es <precio>

    Ejemplos:
      | costo | margen | metodo | precio |
      | 1     | 0.5    | markup | 2      |
      | 3     | 0.5    | markup | 4      |
      | 5     | 0.5    | markup | 8      |
      | 1     | 0.6    | margen | 2      |

  # Desviación documentada: en estos empates exactos Python da 61 y 11337 por el error de float.
  Esquema del escenario: En un empate exacto de medio peso .NET redondea el valor exacto
    Cuando calculo el precio de un costo de <costo> con margen "<margen>" por el método "<metodo>"
    Entonces el precio es <precio>

    Ejemplos:
      | costo | margen | metodo | precio | python |
      | 55    | 0.1    | markup | 60     | 61     |
      | 6349  | 0.44   | margen | 11338  | 11337  |

  Esquema del escenario: Entradas sin sentido de negocio se rechazan
    Cuando calculo el precio de un costo de <costo> con margen "<margen>" por el método "<metodo>"
    Entonces el cálculo se rechaza con el código "PRECIO_INVALIDO" y un mensaje que contiene "<texto>"

    Ejemplos:
      | costo   | margen | metodo   | texto    |
      | 1000000 | 1      | margen   | < 1      |
      | 1000000 | -0.1   | margen   | negativo |
      | 0       | 0.1    | margen   | positivo |
      | 1000000 | 0.1    | promedio | Método   |
