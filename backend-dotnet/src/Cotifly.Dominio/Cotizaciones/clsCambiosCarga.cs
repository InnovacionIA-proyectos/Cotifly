namespace Cotifly.Dominio.Cotizaciones;

/// <summary>
/// Cambios de carga de una re-solicitud del costeo (<c>DatosCargaWeb</c>, HU-013.4, ADR-018 §5): solo los campos de línea que cambian,
/// con los nombres del contrato. Un campo que no viene se conserva; uno que viene en <c>null</c> se borra (<see cref="Presentes"/>
/// distingue los dos casos). <c>posiciones</c>, si viene, reemplaza todas las anteriores. Origen, destino, cliente y línea de negocio no
/// se cambian por esta vía (eso es otra cotización). La combinación resultante se valida igual que en la captura
/// (<see cref="clsSolicitudCosteo.Validar"/>).
/// </summary>
public sealed record clsCambiosCarga
{
    public const string CampoTipoVehiculo = "tipo_vehiculo";
    public const string CampoCarroceria = "carroceria";
    public const string CampoTipologia = "tipologia";
    public const string CampoClaseViaje = "clase_viaje";
    public const string CampoClaseContenedor = "clase_contenedor";
    public const string CampoExclusivo = "exclusivo";
    public const string CampoConsolidado = "consolidado";
    public const string CampoRetornaContenedor = "retorna_contenedor";
    public const string CampoCiudadDevolucionContenedor = "ciudad_devolucion_contenedor";
    public const string CampoCiudadDevolucionMunicipioId = "ciudad_devolucion_municipio_id";
    public const string CampoLevanteContenedorDiferente = "levante_contenedor_diferente";
    public const string CampoCiudadLevanteContenedor = "ciudad_levante_contenedor";
    public const string CampoCiudadLevanteMunicipioId = "ciudad_levante_municipio_id";
    public const string CampoValorAprovisionamiento = "valor_aprovisionamiento";
    public const string CampoHorasCargue = "horas_cargue";
    public const string CampoHorasDescargue = "horas_descargue";
    public const string CampoPesoKg = "peso_kg";
    public const string CampoValorMercancia = "valor_mercancia";
    public const string CampoPosiciones = "posiciones";

    /// <summary>Campos que acepta <c>datos_carga</c> (los de línea de la captura más las horas del encabezado).</summary>
    public static readonly IReadOnlySet<string> Campos = new HashSet<string>(StringComparer.Ordinal)
    {
        CampoTipoVehiculo, CampoCarroceria, CampoTipologia, CampoClaseViaje, CampoClaseContenedor, CampoExclusivo, CampoConsolidado,
        CampoRetornaContenedor, CampoCiudadDevolucionContenedor, CampoCiudadDevolucionMunicipioId, CampoLevanteContenedorDiferente,
        CampoCiudadLevanteContenedor, CampoCiudadLevanteMunicipioId,
        CampoValorAprovisionamiento, CampoHorasCargue, CampoHorasDescargue, CampoPesoKg, CampoValorMercancia, CampoPosiciones,
    };

    /// <summary>Nombres (del contrato) de los campos que trae la petición, incluidos los que vienen en <c>null</c>.</summary>
    public required IReadOnlySet<string> Presentes { get; init; }

    public string? TipoVehiculo { get; init; }
    public string? Carroceria { get; init; }
    public string? Tipologia { get; init; }
    public string? ClaseViaje { get; init; }
    public string? ClaseContenedor { get; init; }
    public bool? Exclusivo { get; init; }
    public bool? Consolidado { get; init; }
    public bool? RetornaContenedor { get; init; }
    public string? CiudadDevolucionContenedor { get; init; }

    /// <summary>HU-028 (#186), defecto B: id del municipio de devolución (<c>GET /web/municipios</c>), como en la captura.</summary>
    public int? CiudadDevolucionMunicipioId { get; init; }

    public bool? LevanteContenedorDiferente { get; init; }
    public string? CiudadLevanteContenedor { get; init; }

    /// <summary>HU-028 (#186), defecto B: id del municipio de levante.</summary>
    public int? CiudadLevanteMunicipioId { get; init; }

    public decimal? ValorAprovisionamiento { get; init; }
    public decimal? HorasCargue { get; init; }
    public decimal? HorasDescargue { get; init; }
    public decimal? PesoKg { get; init; }
    public decimal? ValorMercancia { get; init; }

    /// <summary>Posiciones nuevas (solo si <c>posiciones</c> está en <see cref="Presentes"/>; <c>null</c> = sin posiciones).</summary>
    public IReadOnlyList<clsPosicionCarga>? Posiciones { get; init; }

    /// <summary>¿Trae al menos un campo? (<c>DatosCargaWeb</c> tiene <c>minProperties: 1</c>.)</summary>
    public bool TieneCambios => Presentes.Count > 0;

    /// <summary>Encabezado con las horas de cargue y descargue cambiadas, si vienen.</summary>
    public clsEncabezadoSolicitudCosteo Aplicar(clsEncabezadoSolicitudCosteo encabezado)
    {
        ArgumentNullException.ThrowIfNull(encabezado);
        return encabezado with
        {
            HorasCargue = Valor(CampoHorasCargue, HorasCargue, encabezado.HorasCargue),
            HorasDescargue = Valor(CampoHorasDescargue, HorasDescargue, encabezado.HorasDescargue),
        };
    }

    /// <summary>Línea con los cambios aplicados sobre la guardada (origen y destino nunca cambian).</summary>
    public clsLineaSolicitudCosteo Aplicar(clsLineaSolicitudCosteo linea)
    {
        ArgumentNullException.ThrowIfNull(linea);
        return linea with
        {
            TipoVehiculo = Valor(CampoTipoVehiculo, TipoVehiculo, linea.TipoVehiculo),
            Carroceria = Valor(CampoCarroceria, Carroceria, linea.Carroceria),
            Tipologia = Valor(CampoTipologia, Tipologia, linea.Tipologia),
            ClaseViaje = Valor(CampoClaseViaje, ClaseViaje, linea.ClaseViaje),
            ClaseContenedor = Valor(CampoClaseContenedor, ClaseContenedor, linea.ClaseContenedor),
            Exclusivo = Valor(CampoExclusivo, Exclusivo, linea.Exclusivo),
            Consolidado = Valor(CampoConsolidado, Consolidado, linea.Consolidado),
            RetornaContenedor = Valor(CampoRetornaContenedor, RetornaContenedor, linea.RetornaContenedor),
            CiudadDevolucionContenedor = Valor(CampoCiudadDevolucionContenedor, CiudadDevolucionContenedor, linea.CiudadDevolucionContenedor),
            CiudadDevolucionMunicipioId = Municipio(CampoCiudadDevolucionMunicipioId, CampoCiudadDevolucionContenedor, CiudadDevolucionMunicipioId, linea.CiudadDevolucionMunicipioId),
            LevanteContenedorDiferente = Valor(CampoLevanteContenedorDiferente, LevanteContenedorDiferente, linea.LevanteContenedorDiferente),
            CiudadLevanteContenedor = Valor(CampoCiudadLevanteContenedor, CiudadLevanteContenedor, linea.CiudadLevanteContenedor),
            CiudadLevanteMunicipioId = Municipio(CampoCiudadLevanteMunicipioId, CampoCiudadLevanteContenedor, CiudadLevanteMunicipioId, linea.CiudadLevanteMunicipioId),
            ValorAprovisionamiento = Valor(CampoValorAprovisionamiento, ValorAprovisionamiento, linea.ValorAprovisionamiento),
            PesoKg = Valor(CampoPesoKg, PesoKg, linea.PesoKg),
            ValorMercancia = Valor(CampoValorMercancia, ValorMercancia, linea.ValorMercancia),

            // HU-021: las horas son de la ruta (la cabecera ya no las guarda).
            HorasCargue = Valor(CampoHorasCargue, HorasCargue, linea.HorasCargue),
            HorasDescargue = Valor(CampoHorasDescargue, HorasDescargue, linea.HorasDescargue),
            Posiciones = Presentes.Contains(CampoPosiciones) ? Posiciones ?? [] : linea.Posiciones,
        };
    }

    private T Valor<T>(string campo, T nuevo, T anterior) => Presentes.Contains(campo) ? nuevo : anterior;

    /// <summary>
    /// Defecto B: el id de la ciudad del contenedor. Si viene, el nuevo; si solo cambia el nombre (sin el id), el id anterior ya no le
    /// corresponde y se borra (la revalidación lo exige o lo resuelve); si no cambia nada, se conserva.
    /// </summary>
    private int? Municipio(string campoId, string campoNombre, int? nuevo, int? anterior) =>
        Presentes.Contains(campoId) ? nuevo : Presentes.Contains(campoNombre) ? null : anterior;
}
