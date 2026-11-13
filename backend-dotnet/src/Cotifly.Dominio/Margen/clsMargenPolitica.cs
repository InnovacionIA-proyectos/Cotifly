using Cotifly.Dominio.Comun;

namespace Cotifly.Dominio.Margen;

/// <summary>
/// Margen de política por línea de negocio (<c>dbo_cotifly.MargenesPolitica</c>). Las 8 líneas
/// vienen sembradas por el script 0001; <see cref="Id"/> es la FK <c>Cotizaciones.lineaNegocioId</c>.
/// </summary>
public sealed class clsMargenPolitica
{
    public const string CodigoError = "MARGEN_POLITICA_INVALIDO";
    public const string MetodoMargen = clsReglaMargen.MetodoMargen;
    public const int LargoMaximoLinea = 40;

    private clsMargenPolitica()
    {
    }

    public int Id { get; private set; }
    public string LineaNegocio { get; private set; } = string.Empty;
    public string? MapeoLineasNexo { get; private set; }
    public decimal Margen { get; private set; }
    public string MetodoCalculo { get; private set; } = MetodoMargen;
    public bool Activa { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Crea una línea con margen ≥ 0 (y &lt; 1 si el método es <c>margen</c>, ver <see cref="clsReglaMargen"/>). El
    /// <paramref name="mapeoLineasNexo"/>, si viene, debe ser una de las seis líneas de NEXO (DU §8, <see cref="Cotizaciones.clsLineasNegocioNexo"/>).
    /// </summary>
    public static clsResultado<clsMargenPolitica> Crear(
        string lineaNegocio, decimal margen, string metodoCalculo = MetodoMargen, string? mapeoLineasNexo = null)
    {
        var error = clsValidacion.Primero(
            clsValidacion.Requerido(CodigoError, "lineaNegocio", lineaNegocio, LargoMaximoLinea),
            clsValidacion.Requerido(CodigoError, "metodoCalculo", metodoCalculo),
            clsReglaMargen.Validar(CodigoError, "margen", margen, metodoCalculo),
            Cotizaciones.clsLineasNegocioNexo.EsValida(mapeoLineasNexo)
                ? null
                : new clsErrorDominio(CodigoError,
                    $"El campo 'mapeoLineasNexo' ('{mapeoLineasNexo!.Trim()}') debe ser una de las líneas de negocio de Nexo: {Cotizaciones.clsLineasNegocioNexo.Lista}."));

        if (error is not null)
        {
            return error;
        }

        return new clsMargenPolitica
        {
            LineaNegocio = lineaNegocio.Trim(),
            Margen = margen,
            MetodoCalculo = metodoCalculo.Trim(),
            MapeoLineasNexo = clsValidacion.Normalizar(mapeoLineasNexo),
            Activa = true,
        };
    }

    /// <summary>Cambia el margen de la línea (administración de líneas).</summary>
    public clsResultado<clsMargenPolitica> CambiarMargen(decimal margen)
    {
        if (clsReglaMargen.Validar(CodigoError, "margen", margen, MetodoCalculo) is { } error)
        {
            return error;
        }

        Margen = margen;
        return this;
    }

    /// <summary>
    /// Edición de la línea desde la administración (<c>PUT /web/admin/lineas-negocio/{id}</c>): margen, método y
    /// estado a la vez. El método debe ser <c>margen</c> o <c>markup</c> y el margen válido para ese método
    /// (<see cref="clsReglaMargen"/>); si algo falla la línea no cambia.
    /// </summary>
    public clsResultado<clsMargenPolitica> Actualizar(decimal margen, string metodoCalculo, bool activa)
    {
        var metodo = metodoCalculo?.Trim().ToLowerInvariant();
        if (metodo is not (clsReglaMargen.MetodoMargen or clsReglaMargen.MetodoMarkup))
        {
            return new clsErrorDominio(CodigoError, "El método de cálculo debe ser 'margen' o 'markup'.");
        }

        if (clsReglaMargen.Validar(CodigoError, "margen", margen, metodo) is { } error)
        {
            return error;
        }

        Margen = margen;
        MetodoCalculo = metodo;
        Activa = activa;
        return this;
    }

    public void Activar() => Activa = true;

    public void Desactivar() => Activa = false;
}
