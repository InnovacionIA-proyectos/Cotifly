# Cómo calcula Cotifly el precio de un flete (estado al 2026-10-30)

> Nota para evaluar después. Se armó leyendo el código del backend en la rama QA. Si algo no cuadra con lo que espera negocio, ese es el punto a revisar.

## 1. Cómo se organiza una cotización

- Una **cotización** tiene una o varias **rutas** (por ejemplo, Medellín → Bogotá).
- Cada ruta lleva uno o varios **camiones**.

## 2. El costo lo pone Nexo, no Cotifly

Cotifly manda la solicitud a Nexo. Nexo devuelve el **costo de cada camión**, de una de dos formas:

- **Autocosteo:** Nexo lo calcula solo.
- **Entregada:** lo costea la Mesa de Costeo.

Cotifly va consultando a Nexo (polling) hasta que llega ese costo.

## 3. Costo de la ruta = suma de todo

Código: `clsLineaSolicitud.SumarCostos` (`backend-dotnet/src/Cotifly.Dominio/Cotizaciones/clsLineaSolicitud.cs`, línea ~941).

```
Costo de la ruta = fletes de los camiones
                 + retorno del contenedor   (si aplica)
                 + levante del contenedor   (si aplica)
                 + servicios conexos        (escolta, precinto, etc.)
```

## 4. Encima del costo se aplica el margen

**De dónde sale el margen sugerido:**

1. **SAP TM**, que da un margen por cliente (sincronizado en `SapMargenCliente`, HU-042.1).
2. Si SAP no tiene margen para ese cliente, o el que tiene es anómalo (fuera de 0–60 %), se usa el de la **política de la línea de negocio**.
3. El comercial lo puede **ajustar** (queda como `negociado`). Si lo deja por debajo del sugerido, tiene que escribir una **justificación**.

**La fórmula tiene dos métodos.** Código: `clsCalculadoraPrecio.Calcular` (`backend-dotnet/src/Cotifly.Dominio/Precio/clsCalculadoraPrecio.cs`).

| Método | Fórmula | Qué significa |
|---|---|---|
| **margen** (el que se usa por defecto) | `precio = costo / (1 − margen)` | El margen es el % de ganancia **sobre el precio de venta**. Debe ser menor que 100 %. |
| **markup** | `precio = costo × (1 + margen)` | El margen es un recargo **sobre el costo**. |

El precio se redondea a pesos enteros.

## 5. Precio de la cotización

- Es la **suma del precio de todas las rutas**.
- Solo aparece cuando **todas** las rutas ya tienen precio.
- Las rutas **rechazadas** por la Mesa de Costeo no cuentan.

## 6. Cotizaciones en dólares

El precio se calcula en pesos y al final se convierte con la **TRM fijada en la solicitud**.

## 7. Ejemplo

Una ruta con un camión, un precinto satelital y margen del 20 %:

| Concepto | Valor |
|---|---|
| Flete del camión (lo da Nexo) | $2.000.000 |
| Precinto satelital (conexo) | $85.000 |
| **Costo de la ruta** | **$2.085.000** |
| Precio con método **margen** 20 %: 2.085.000 / 0,80 | **$2.606.250** (ganancia $521.250) |
| Precio con método **markup** 20 %: 2.085.000 × 1,20 | $2.502.000 (ganancia $417.000) |

Con el mismo 20 %, el markup deja menos ganancia porque el porcentaje se calcula sobre el costo y no sobre el precio.

## 8. Lo que va a cambiar con HU-043 (#251, pendiente)

**Hoy** el margen se aplica **también a los conexos**, porque van dentro del costo de la ruta.

Con HU-043, los conexos dejan de llevar margen y se cobran al **precio de venta que tenga Nexo**:

```
Precio de la ruta = (flete + retorno + levante) con margen  +  conexos a precio de venta
```

Mismo ejemplo: 2.000.000 / 0,80 = $2.500.000, más el precinto a su precio de venta de Nexo.

## 9. Preguntas para evaluar

- [ ] ¿Negocio está de acuerdo con que los conexos se cobren sin margen (HU-043)? ¿O hay conexos que sí deberían llevarlo?
- [ ] ¿Cuándo se usa **margen** y cuándo **markup**? ¿Quién lo decide?
- [ ] Con la política de seguros (`2026-10-30_POLITICA_SEGUROS_NOGE0076.md`): los conexos obligatorios por valor de mercancía (acompañante, precinto) ¿se muestran **aparte** del flete en la cotización, o **sumados en un solo total**?
- [ ] ¿De dónde saca Nexo el costo de cada camión? Pendiente de revisar.
