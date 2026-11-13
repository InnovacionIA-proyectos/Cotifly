using Cotifly.Dominio.Comun;
using Cotifly.Dominio.Precio;

namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// El seguro de la mercancía de la ruta (ADR-032; póliza específica,
/// NOGE0076). Se marca en el costeo, se calcula en pesos con la tasa vigente copiada en la ruta y se suma al precio sin margen. Las reglas
/// que exige la base (CHECK del script 20): sin seguro, la tasa y el costo van en <c>null</c>; con costo hay tasa; el costo no es negativo.
/// </summary>
public sealed partial class clsLineaSolicitud
{
    /// <summary>
    /// <c>valorMercancia × tasa</c> en pesos, exacto y sin redondeo (el valor
    /// de la mercancía es siempre en pesos). Lo que se guarda en la ruta es <see cref="SeguroGuardado"/>.
    /// </summary>
    public static decimal CalcularSeguro(decimal valorMercancia, decimal tasa) => valorMercancia * tasa;

    /// <summary>Escala de <c>CotizacionLineasSolicitud.costoSeguro</c> (<c>DECIMAL(18,2)</c>, script 20): centavos.</summary>
    public const int DecimalesCostoSeguro = 2;

    /// <summary>
    /// El seguro tal como lo guarda la columna <c>costoSeguro DECIMAL(18,2)</c>: <see cref="CalcularSeguro"/> sin redondear a
    /// pesos, llevado a la escala de la columna (centavos) con el mismo criterio con que SQL Server/SqlClient ajustan un decimal de más
    /// escala (la mitad se aleja de cero). Así el seguro en memoria es exactamente el que se relee de la base.
    /// </summary>
    public static decimal SeguroGuardado(decimal valorMercancia, decimal tasa) =>
        Math.Round(CalcularSeguro(valorMercancia, tasa), DecimalesCostoSeguro, MidpointRounding.AwayFromZero);

    /// <summary>
    /// El seguro de la ruta en la moneda de la solicitud: <see cref="CostoSeguro"/> en pesos o, en dólares, convertido con la
    /// <paramref name="trm"/> fijada en la solicitud (como el resto del precio). <paramref name="valor"/> queda en <c>null</c> sin
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
    /// Incluye el seguro con la <paramref name="tasaVigente"/> (la activa de <c>TarifasSeguro</c>). Sin
    /// <see cref="ValorMercancia"/> responde <c>SEGURO_SIN_VALOR_MERCANCIA</c> y nada cambia. Si ya estaba incluido no cambia
    /// nada: conserva su tasa aunque la vigente sea otra (un cambio de tasa no mueve lo ya cotizado). No recalcula el precio: desde fuera se
    /// usa <see cref="clsCotizacionSolicitud.CambiarSeguro"/>. Devuelve si cambió.
    /// </summary>
    internal clsResultado<bool> IncluirSeguro(decimal tasaVigente)
    {
        // Marcar una ruta que ya lo tiene es idempotente aunque no traiga tasa (no se usa).
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
        CostoSeguro = SeguroGuardado(valor, tasaVigente);
        return true;
    }

    /// <summary>Quita el seguro (la tasa y el costo quedan en <c>null</c>, como exige la base). Devuelve si cambió.</summary>
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
    /// El valor de la mercancía de la ruta pasó de <paramref name="valorAnterior"/> al actual. Si la ruta incluye
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
        var (tasaNueva, costoNuevo) = ValorMercancia is { } valor && tasa is { } t ? (t, SeguroGuardado(valor, t)) : ((decimal?)null, (decimal?)null);
        var cambio = tasaNueva != TasaSeguroAplicada || costoNuevo != CostoSeguro;
        TasaSeguroAplicada = tasaNueva;
        CostoSeguro = costoNuevo;
        return cambio;
    }

    /// <summary>
    /// Versión nueva (<c>-vN</c> de la re-solicitud o de la edición): la ruta equivalente de la versión anterior incluía el
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
