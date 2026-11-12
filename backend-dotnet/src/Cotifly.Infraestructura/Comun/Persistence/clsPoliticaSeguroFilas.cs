namespace Cotifly.Infraestructura.Comun.Persistence;

/// <summary>
/// Fila de <c>dbo_cotifly.PoliticaSeguroTramos</c> (<c>db/scripts/21_politica_seguros.sql</c>): un rango del
/// valor de la mercancía de la ruta, (<see cref="ValorDesde"/>, <see cref="ValorHasta"/>]; <see cref="ValorHasta"/> <c>null</c> = sin tope.
/// La escribe Datos (<c>db/scripts/operacion/cambiar_politica_seguros.sql</c>); EF solo la lee (<c>AsNoTracking</c>).
/// </summary>
public sealed class clsPoliticaSeguroTramoFila
{
    public Guid Id { get; private set; }

    public string Nombre { get; private set; } = string.Empty;

    /// <summary>Exclusivo.</summary>
    public decimal ValorDesde { get; private set; }

    /// <summary>Incluido; <c>null</c> = sin tope.</summary>
    public decimal? ValorHasta { get; private set; }

    /// <summary><c>isActive</c>: solo cuentan los activos.</summary>
    public bool Activo { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }
}

/// <summary>Fila de <c>dbo_cotifly.PoliticaSeguroMedidas</c> (script 21): el catálogo de medidas (<see cref="Codigo"/> único).</summary>
public sealed class clsPoliticaSeguroMedidaFila
{
    public Guid Id { get; private set; }

    public string Codigo { get; private set; } = string.Empty;

    /// <summary>Lo que se muestra al comercial.</summary>
    public string Nombre { get; private set; } = string.Empty;

    /// <summary>Informativo: la medida se cumple agregando un conexo de Nexo (los acompañantes). El backend usa los conexos de la medida.</summary>
    public bool EsCobrable { get; private set; }

    /// <summary><c>isActive</c>: solo cuentan las activas.</summary>
    public bool Activa { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }
}

/// <summary>
/// Fila de <c>dbo_cotifly.PoliticaSeguroTramoMedidas</c> (script 21): la medida que aplica en un tramo y su
/// <see cref="Nivel"/> (<c>recomendada</c> | <c>obligatoria</c>). Sin <c>isActive</c>: si la fila no está, la medida no aplica en el tramo.
/// </summary>
public sealed class clsPoliticaSeguroTramoMedidaFila
{
    public Guid Id { get; private set; }

    public Guid TramoId { get; private set; }

    public Guid MedidaId { get; private set; }

    public string Nivel { get; private set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; private set; }
}

/// <summary>
/// Fila de <c>dbo_cotifly.PoliticaSeguroMedidaConexos</c> (script 21): un <c>codigoServicio</c> del catálogo de Nexo que
/// cumple la medida (sin FK: es de otra base).
/// </summary>
public sealed class clsPoliticaSeguroMedidaConexoFila
{
    public Guid Id { get; private set; }

    public Guid MedidaId { get; private set; }

    public string CodigoServicio { get; private set; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; private set; }
}
