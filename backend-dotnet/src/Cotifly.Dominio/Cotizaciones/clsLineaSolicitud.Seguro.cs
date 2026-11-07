using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Precio;

namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// HU-045.2 (#264, ADR-029; script <c>db/scripts/20_seguro_mercancia.sql</c>): el seguro de la mercancía de la ruta (póliza específica,
/// NOGE0076). Se marca en el costeo, se calcula en pesos con la tasa vigente copiada en la ruta y se suma al precio sin margen. Las reglas
/// que exige la base (CHECK del 20): sin seguro, la tasa y el costo van en <c>null</c>; con costo hay tasa; el costo no es negativo.
/// </summary>
public sealed partial class clsLineaSolicitud
{
    /// <summary>
    /// HU-045.2 (#264): <c>round(valorMercancia × tasa, 0)</c> en pesos enteros (la fórmula de la HU; el valor de la mercancía es siempre
    /// en pesos, decisión 2).
    /// </summary>
    public static decimal CalcularSeguro(decimal valorMercancia, decimal tasa) =>
        clsConversorMoneda.Redondear(valorMercancia * tasa, clsConversorMoneda.Pesos);

    /// <summary>
    /// HU-045.2 (#264): el seguro de la ruta en la moneda de la solicitud: <see cref="CostoSeguro"/> en pesos o, en dólares, convertido con la
    /// <paramref name="trm"/> fijada en la solicitud (decisión 2, como el resto del precio). <paramref name="valor"/> queda en <c>null</c> sin
    /// seguro o sin costo calculado (ruta sin valor de la mercancía). Devuelve el error del módulo de precio (TRM inválida), o <c>null</c>.
    /// </summary>
    public clsErrorDominio? ValorSeguro(string? moneda, decimal trm, out decimal? valor)
    {
        valor = null;
        if (!IncluyeSeguro || CostoSeguro is not { } enPesos)
        {
            return null;
        }

        if (clsConversorMoneda.EsPesos(moneda))
        {
            valor = enPesos;
            return null;
        }

        var convertido = clsConversorMoneda.PesosADolares(enPesos, trm);
        if (convertido.EsFallo)
        {
            return convertido.Error;
        }

        valor = convertido.Valor;
        return null;
    }

    /// <summary>
    /// HU-045.2 (#264): incluye el seguro con la <paramref name="tasaVigente"/> (la activa de <c>TarifasSeguro</c>). Sin
    /// <see cref="ValorMercancia"/> responde <c>SEGURO_SIN_VALOR_MERCANCIA</c> (decisión 4) y nada cambia. Si ya estaba incluido no cambia
    /// nada: conserva su tasa aunque la vigente sea otra (un cambio de tasa no mueve lo ya cotizado). No recalcula el precio: desde fuera se
    /// usa <see cref="clsCotizacionSolicitud.CambiarSeguro"/>. Devuelve si cambió.
    /// </summary>
    internal clsResultado<bool> IncluirSeguro(decimal tasaVigente)
    {
        // D1 de la validación: marcar una ruta que ya lo tiene es idempotente aunque no traiga tasa (no se usa).
        if (IncluyeSeguro)
        {
            return false;
        }

        ValidarTasa(tasaVigente);

        if (ValorMercancia is not { } valor)
        {
            return new clsErrorDominio(clsCodigosError.SeguroSinValorMercancia,
                $"La ruta {Orden} no tiene valor de la mercancía: el seguro es el valor de la mercancía por la tasa vigente. Edita la solicitud "
                + "con el valor y vuelve a marcarlo.")
            {
                Datos = new Dictionary<string, object?>(StringComparer.Ordinal) { ["linea_orden"] = Orden, ["linea_solicitud_id"] = Id },
            };
        }

        IncluyeSeguro = true;
        TasaSeguroAplicada = tasaVigente;
        CostoSeguro = CalcularSeguro(valor, tasaVigente);
        return true;
    }

    /// <summary>HU-045.2 (#264): quita el seguro (la tasa y el costo quedan en <c>null</c>, como exige la base). Devuelve si cambió.</summary>
    internal bool QuitarSeguro()
    {
        if (!IncluyeSeguro && TasaSeguroAplicada is null && CostoSeguro is null)
        {
            return false;
        }

        IncluyeSeguro = false;
        TasaSeguroAplicada = null;
        CostoSeguro = null;
        return true;
    }

    /// <summary>
    /// HU-045.2 (#264, decisión 5): el valor de la mercancía de la ruta pasó de <paramref name="valorAnterior"/> al actual. Si la ruta incluye
    /// el seguro y el valor cambió, vuelve a tomar la tasa (<paramref name="tasaVigente"/>; sin ella, la que ya tenía la ruta) y recalcula el
    /// costo; sin valor, el seguro sigue marcado pero sin tasa ni costo (no suma al precio). No recalcula el precio. Devuelve si cambió.
    /// </summary>
    internal bool ActualizarSeguroPorValor(decimal? valorAnterior, decimal? tasaVigente)
    {
        if (!IncluyeSeguro || valorAnterior == ValorMercancia)
        {
            return false;
        }

        if (tasaVigente is { } vigente)
        {
            ValidarTasa(vigente);
        }

        var tasa = tasaVigente ?? TasaSeguroAplicada;
        var (tasaNueva, costoNuevo) = ValorMercancia is { } valor && tasa is { } t ? (t, CalcularSeguro(valor, t)) : ((decimal?)null, (decimal?)null);
        var cambio = tasaNueva != TasaSeguroAplicada || costoNuevo != CostoSeguro;
        TasaSeguroAplicada = tasaNueva;
        CostoSeguro = costoNuevo;
        return cambio;
    }

    /// <summary>
    /// HU-045.2 (#264): versión nueva (<c>-vN</c> de la re-solicitud o de la edición): la ruta equivalente de la versión anterior incluía el
    /// seguro, así que esta también, con la misma tasa y el mismo costo; si el valor de la mercancía cambió, se recalcula con la
    /// <paramref name="tasaVigente"/> (<see cref="ActualizarSeguroPorValor"/>), como el margen negociado que se copia.
    /// </summary>
    internal void CopiarSeguro(clsLineaSolicitud anterior, decimal? tasaVigente)
    {
        ArgumentNullException.ThrowIfNull(anterior);
        if (!anterior.IncluyeSeguro)
        {
            return;
        }

        IncluyeSeguro = true;
        TasaSeguroAplicada = anterior.TasaSeguroAplicada;
        CostoSeguro = anterior.CostoSeguro;
        ActualizarSeguroPorValor(anterior.ValorMercancia, tasaVigente);
    }

    /// <summary>La tasa es una fracción entre 0 y 1 sin incluirlos (CHECK de <c>TarifasSeguro.tasa</c>); otra es un error de programación.</summary>
    private static void ValidarTasa(decimal tasa)
    {
        if (tasa is <= 0m or >= 1m)
        {
            throw new ArgumentOutOfRangeException(nameof(tasa), "La tasa del seguro es una fracción mayor que 0 y menor que 1 (0.003450 = 0,345 %).");
        }
    }
}
