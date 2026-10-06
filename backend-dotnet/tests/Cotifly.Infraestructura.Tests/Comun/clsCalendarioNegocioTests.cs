using Cotifly.Aplicacion.Comun.Interfaces;
using Cotifly.Infraestructura.Comun;
using Cotifly.Infraestructura.Configuracion;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Cotifly.Infraestructura.Tests.Comun;

/// <summary>Fechas de negocio en America/Bogota (UTC−5, sin horario de verano) para los límites y meses del tablero (HU-010.1).</summary>
public sealed class clsCalendarioNegocioTests
{
    private static clsCalendarioNegocio Calendario(DateTimeOffset ahora)
    {
        var reloj = Substitute.For<IClsReloj>();
        reloj.AhoraUtc.Returns(ahora);
        return new clsCalendarioNegocio(reloj, Options.Create(new clsOpcionesZona()));
    }

    [Fact]
    public void AntesDeMedianocheEnBogotaSigueSiendoElDiaAnterior()
    {
        var calendario = Calendario(new DateTimeOffset(2026, 10, 1, 3, 30, 0, TimeSpan.Zero));

        calendario.Hoy.Should().Be(new DateOnly(2026, 9, 30));
        calendario.FechaDe(new DateTimeOffset(2026, 10, 1, 4, 59, 0, TimeSpan.Zero)).Should().Be(new DateOnly(2026, 9, 30));
        calendario.FechaDe(new DateTimeOffset(2026, 10, 1, 5, 0, 0, TimeSpan.Zero)).Should().Be(new DateOnly(2026, 10, 1));
        calendario.ZonaHoraria.Should().Be("America/Bogota");
    }

    [Fact]
    public void ElDiaEmpiezaALasCincoUtc()
    {
        var inicio = Calendario(DateTimeOffset.UtcNow).InicioDelDia(new DateOnly(2026, 1, 1));

        inicio.Should().Be(new DateTimeOffset(2026, 1, 1, 5, 0, 0, TimeSpan.Zero));
        inicio.Offset.Should().Be(TimeSpan.FromHours(-5));
    }
}
