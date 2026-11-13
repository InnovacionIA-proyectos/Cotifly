using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Aplicacion.Cotizaciones.Repositories;
using Cotifly.Dominio.Cotizaciones;
using Cotifly.Pruebas.Comun;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace Cotifly.Infraestructura.Tests.Persistencia;

/// <summary>
/// La finalización de la cotización contra SQL Server 2019 con <c>db/scripts/14_cotizacion_finalizada.sql</c>: la finalización se guarda y se lee
/// (<c>finalizadaAt</c>, <c>finalizadaPor</c> con su FK a <c>UsuariosWeb</c>), el CHECK exige las dos columnas juntas, y la concurrencia con
/// el polling: quien confirma primero con la versión vigente gana y el otro no escribe nada.
/// </summary>
[Collection(clsColeccionSqlServer.Nombre)]
[Trait("Categoria", "Integracion")]
public sealed class clsCotizacionFinalizadaPersistenciaTests(clsBaseDatosPruebas baseDatos) : clsPruebaIntegracion(baseDatos)
{
    [Fact]
    public async Task LaFinalizacionSeGuardaConLaVersionYSeLeeDeVuelta()
    {
        var (cotizacion, usuario) = await CosteadaAsync();

        var guardado = await FinalizarAsync(cotizacion.Id, usuario, versionEsperada: 0);

        guardado.Should().BeTrue();
        (await clsBaseDatosPruebas.EscalarAsync<string>(BaseDatos.CadenaCotifly,
                $"SELECT CONCAT(versionNegocio, '|', finalizadaPor, '|', IIF(finalizadaAt IS NULL, 'sin', 'con')) FROM dbo_cotifly.CotizacionesSolicitud WHERE id = '{cotizacion.Id}'"))
            .Should().Be($"1|{usuario.ToString().ToUpperInvariant()}|con");
        var leida = await ConsultarAsync(s => s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorIdAsync(cotizacion.Id, Ct));
        leida!.Finalizada.Should().BeTrue();
        leida.FinalizadaPor.Should().Be(usuario);
        leida.FinalizadaAt.Should().Be(Reloj.AhoraUtc);
        leida.EstadoCosteo.Should().Be(EstadoCosteo.Autocosteo);
    }

    [Fact]
    public async Task ElCheckExigeLaFechaYElComercialJuntosYLaFkUnUsuarioQueExista()
    {
        var (cotizacion, usuario) = await CosteadaAsync();

        var sinComercial = () => clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly,
            $"UPDATE dbo_cotifly.CotizacionesSolicitud SET finalizadaAt = SYSDATETIMEOFFSET() WHERE id = '{cotizacion.Id}'");
        var sinFecha = () => clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly,
            $"UPDATE dbo_cotifly.CotizacionesSolicitud SET finalizadaPor = '{usuario}' WHERE id = '{cotizacion.Id}'");
        var usuarioInexistente = () => clsBaseDatosPruebas.EjecutarAsync(BaseDatos.CadenaCotifly,
            $"UPDATE dbo_cotifly.CotizacionesSolicitud SET finalizadaAt = SYSDATETIMEOFFSET(), finalizadaPor = '{Guid.NewGuid()}' WHERE id = '{cotizacion.Id}'");

        (await sinComercial.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_cotizaciones_finalizada");
        (await sinFecha.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("ck_cotizaciones_finalizada");
        (await usuarioInexistente.Should().ThrowAsync<SqlException>()).Which.Message.Should().Contain("fk_cotizaciones_finalizadaPor");
    }

    [Fact]
    public async Task SiLaFinalizacionConfirmaPrimeroElPollingQueSeCruzoNoGuardaElPrecioNuevo()
    {
        var (cotizacion, usuario) = await CosteadaAsync();
        var precio = cotizacion.PrecioFinal;

        // El polling carga la cotización (versión 0) y aplica un flete nuevo de Nexo...
        await using var polling = NuevoAmbito();
        var vistaPolling = await polling.ServiceProvider.GetRequiredService<IClsCotizacionRepository>().ObtenerPorIdAsync(cotizacion.Id, Ct);
        vistaPolling!.Vehiculos[0].RegistrarCosteo(EstadoCosteo.Autocosteo, 3_000_000m, null);
        vistaPolling.RecalcularTotales(Reloj.AhoraUtc);
        vistaPolling.IncrementarVersionNegocio();

        // ...mientras el comercial finaliza con la versión que leyó (0).
        var finalizada = await FinalizarAsync(cotizacion.Id, usuario, versionEsperada: 0);
        var pollingGuardo = await polling.ServiceProvider.GetRequiredService<IClsUnitOfWork>().GuardarCambiosSiVersionAsync(cotizacion.Id, 0, Ct);

        finalizada.Should().BeTrue();
        pollingGuardo.Should().BeFalse("el polling pierde y repite la consulta, ya con la cotización finalizada (precio congelado, #208)");
        (await clsBaseDatosPruebas.EscalarAsync<decimal>(BaseDatos.CadenaCotifly,
                $"SELECT precioFinal FROM dbo_cotifly.CotizacionesSolicitud WHERE id = '{cotizacion.Id}'"))
            .Should().Be(precio!.Value, "el precio finalizado es el que vio el comercial");
    }

    [Fact]
    public async Task SiElPollingConfirmaPrimeroLaFinalizacionConLaVersionViejaNoEscribeNada()
    {
        var (cotizacion, usuario) = await CosteadaAsync();

        var pollingGuardo = await ConsultarAsync(async s =>
        {
            var propia = await s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorIdAsync(cotizacion.Id, Ct);
            propia!.Vehiculos[0].RegistrarCosteo(EstadoCosteo.Autocosteo, 3_000_000m, null);
            propia.RecalcularTotales(Reloj.AhoraUtc);
            propia.IncrementarVersionNegocio();
            return await s.GetRequiredService<IClsUnitOfWork>().GuardarCambiosSiVersionAsync(propia.Id, 0, Ct);
        });
        var finalizada = await FinalizarAsync(cotizacion.Id, usuario, versionEsperada: 0);

        pollingGuardo.Should().BeTrue();
        finalizada.Should().BeFalse("el comercial no vio el precio nuevo: CONFLICTO_CONCURRENCIA y recarga");
        (await clsBaseDatosPruebas.EscalarAsync<int>(BaseDatos.CadenaCotifly,
                $"SELECT COUNT(*) FROM dbo_cotifly.CotizacionesSolicitud WHERE id = '{cotizacion.Id}' AND finalizadaAt IS NULL AND finalizadaPor IS NULL"))
            .Should().Be(1);
    }

    /// <summary>Formulario costeado en <c>autocosteo</c> de un comercial real, guardado; devuelve la cotización y el comercial.</summary>
    private async Task<(clsCotizacionSolicitud Cotizacion, Guid Usuario)> CosteadaAsync()
    {
        var usuario = await CrearUsuarioAsync();
        var cotizacion = clsCotizacionMuestra.Costeada(usuarioId: usuario, folio: FolioUnico(), lineaNegocioId: 1);
        await GuardarAsync(cotizacion);
        return (cotizacion, usuario);
    }

    /// <summary>Lo que hace el caso de uso: carga, finaliza, sube la versión y confirma con la versión esperada.</summary>
    private Task<bool> FinalizarAsync(Guid id, Guid usuario, int versionEsperada) =>
        ConsultarAsync(async s =>
        {
            var propia = await s.GetRequiredService<IClsCotizacionRepository>().ObtenerPorIdAsync(id, Ct);
            propia!.Finalizar(usuario, Reloj.AhoraUtc).Valor.Should().BeTrue();
            propia.IncrementarVersionNegocio();
            return await s.GetRequiredService<IClsUnitOfWork>().GuardarCambiosSiVersionAsync(id, versionEsperada, Ct);
        });
}
