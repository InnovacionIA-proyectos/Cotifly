namespace Cotifly.Dominio.Cotizaciones;

/// <summary>HU-028 (#186, ADR-021): la ruta toma el contenido editado sin perder su id, su línea de negocio ni su margen.</summary>
public sealed partial class clsLineaSolicitud
{
    /// <summary>
    /// Aplica a la ruta guardada el contenido de la edición (ya validado): orden, origen y destino (id y nombre), condiciones, ciudades del
    /// contenedor (id y nombre), horas (las de la ruta o, si no trae, las del <paramref name="encabezado"/>), valores, peso y mercancías
    /// (actualizadas en su lugar: la base exige un orden único por ruta). Los camiones se reemplazan por los del plan si
    /// <paramref name="replanificada"/>; si no, se conservan (con el levante nuevo copiado a cada uno, decisión 4). Los conexos quedan
    /// exactamente los de la edición, con el costo del catálogo de hoy. La línea de negocio, el margen y el id no cambian. No recalcula.
    /// HU-028.1 (#183): con <paramref name="reincluirRechazados"/> (y camiones conservados) los camiones que la Mesa de Costeo rechazó
    /// olvidan el rechazo y vuelven a viajar (<see cref="ReincluirRechazados"/>); sin él siguen rechazados y fuera del envío.
    /// </summary>
    internal void AplicarEdicion(
        int orden, clsRutaCapturada capturada, clsEncabezadoSolicitudCosteo? encabezado, bool replanificada, bool reincluirRechazados = false)
    {
        ArgumentNullException.ThrowIfNull(capturada);
        ArgumentOutOfRangeException.ThrowIfLessThan(orden, 1);
        var linea = capturada.Linea;
        Orden = orden;
        OrigenMunicipioId = linea.OrigenMunicipioId;
        OrigenNombre = NombreMunicipio(linea.Origen);
        DestinoMunicipioId = linea.DestinoMunicipioId;
        DestinoNombre = NombreMunicipio(linea.Destino);
        Tipologia = Normalizar(linea.Tipologia);
        ClaseViaje = Normalizar(linea.ClaseViaje);
        ClaseContenedor = Normalizar(linea.ClaseContenedor);
        Exclusivo = linea.Exclusivo;
        Consolidado = linea.Consolidado;
        RetornaContenedor = linea.RetornaContenedor ?? false;
        CiudadDevolucionMunicipioId = linea.CiudadDevolucionMunicipioId;
        CiudadDevolucionNombre = NombreMunicipio(linea.CiudadDevolucionContenedor);
        LevanteContenedorDiferente = linea.LevanteContenedorDiferente ?? false;
        CiudadLevanteMunicipioId = linea.CiudadLevanteMunicipioId;
        CiudadLevanteNombre = NombreMunicipio(linea.CiudadLevanteContenedor);
        HorasCargue = linea.HorasCargue ?? encabezado?.HorasCargue;
        HorasDescargue = linea.HorasDescargue ?? encabezado?.HorasDescargue;
        PesoKg = linea.PesoKg;
        ValorMercancia = linea.ValorMercancia;
        PesoConTaraKg = linea.PesoConTaraKg;
        // HU-032 (#197): la edición conserva (o cambia) el vehículo elegido de la ruta.
        TipoVehiculoElegido = Normalizar(linea.TipoVehiculo);
        CarroceriaElegida = Normalizar(linea.Carroceria);
        ReemplazarMercancias(linea.Posiciones);

        if (replanificada)
        {
            // Los camiones nuevos copian el levante de la ruta al crearse.
            ValorLevanteContenedor = linea.ValorAprovisionamiento;
            ReemplazarCamiones(capturada.Camiones, capturada.Cubicacion, capturada.AvisosVehiculo);
        }
        else
        {
            CambiarValorLevante(linea.ValorAprovisionamiento);
            if (reincluirRechazados)
            {
                ReincluirRechazados();
            }
        }

        var conexos = capturada.Conexos ?? [];
        ConservarConexos(conexos.Select(c => c.IdServicioConexo).ToHashSet(StringComparer.OrdinalIgnoreCase));
        foreach (var conexo in conexos)
        {
            AgregarOActualizarConexo(conexo);
        }
    }

    /// <summary>
    /// Deja exactamente <paramref name="posiciones"/> como mercancías de la ruta, en su orden: la mercancía de cada orden que ya existe se
    /// actualiza en su lugar, las que sobran se quitan y las que faltan se agregan (así nunca se repite un orden por ruta al guardar).
    /// </summary>
    private void ReemplazarMercancias(IReadOnlyList<clsPosicionCarga> posiciones)
    {
        var actuales = MercanciasOrdenadas;
        var nuevas = clsMercancia.DesdeCaptura(Id, posiciones);
        for (var i = 0; i < actuales.Count; i++)
        {
            if (i < posiciones.Count)
            {
                actuales[i].Actualizar(i + 1, posiciones[i]);
            }
            else
            {
                _mercancias.Remove(actuales[i]);
            }
        }

        _mercancias.AddRange(nuevas.Skip(actuales.Count));
    }
}
