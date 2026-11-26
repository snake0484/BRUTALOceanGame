// A body's atmosphere, single scattering (Rayleigh + Mie) with the sun's transmittance from a Chapman approximation.
// Shared by body.frag (impostors, the rim) and terrain.frag (the octree seen from altitude), so both sides of the
// impostor switch get the same air. Positions are from the body's centre, in any one set of axes.
// The includer defines sunVisibility: the star's visible share at p (eclipses), 1 for none.

const int ATMOSPHERE_SAMPLES = 8;
const vec3 RAYLEIGH = vec3(5.8e-6, 13.5e-6, 33.1e-6);
const float MIE = 4e-6;
const float MIE_EXTINCTION = 1.1;
const float MIE_HEIGHT = 0.15;
const float MIE_G = 0.76;
const float PI = 3.14159265;

float sunVisibility(vec3 p);

// Column density toward the sun relative to the ground's, in scale heights (Schueler's Chapman approximation);
// x = base radius / scale height, h = height / scale height. The caller handles the body's shadow.
float chapman(float x, float h, float mu)
{
    float c = sqrt(0.5 * PI * (x + h));

    if (mu >= 0.0)
        return c / (c * mu + 1.0) * exp(-h);

    float x0 = sqrt(1.0 - mu * mu) * (x + h);
    float c0 = sqrt(0.5 * PI * x0);
    return 2.0 * c0 * exp(x - x0) - c / (1.0 - c * mu) * exp(-h);
}

// Light scattered toward the eye along o + [t0, t1] * dir and the transmittance across it. base = the radius where
// density is 1 (sea level or the datum), scale = scale height, L = toward the star, intensity = the star's light.
vec3 atmosphereScatter(vec3 o, vec3 dir, float t0, float t1, float base, float scale, vec3 L, float intensity, out vec3 transmittance)
{
    float x = base / scale;
    float ds = (t1 - t0) / float(ATMOSPHERE_SAMPLES);
    vec3 rayleigh = vec3(0.0);
    vec3 mie = vec3(0.0);
    float depthR = 0.0;
    float depthM = 0.0;

    for (int i = 0; i < ATMOSPHERE_SAMPLES; i++)
    {
        vec3 p = o + (t0 + (float(i) + 0.5) * ds) * dir;
        float r = length(p);
        float h = max(r - base, 0.0) / scale;
        float densityR = exp(-h);
        float densityM = exp(-h / MIE_HEIGHT);
        depthR += densityR * ds;
        depthM += densityM * ds;
        float mu = dot(p, L) / r;

        if (mu < 0.0 && r * sqrt(1.0 - mu * mu) < base)
            continue;

        float sunR = scale * chapman(x, h, mu);
        float sunM = scale * MIE_HEIGHT * chapman(x / MIE_HEIGHT, h / MIE_HEIGHT, mu);
        vec3 t = exp(-(RAYLEIGH * (depthR + sunR) + MIE * MIE_EXTINCTION * (depthM + sunM))) * sunVisibility(p) * ds;
        rayleigh += densityR * t;
        mie += densityM * t;
    }

    transmittance = exp(-(RAYLEIGH * depthR + MIE * MIE_EXTINCTION * depthM));
    float cosine = dot(dir, L);
    float phaseR = 3.0 / (16.0 * PI) * (1.0 + cosine * cosine);
    float g2 = MIE_G * MIE_G;
    float phaseM = (1.0 - g2) / (4.0 * PI * pow(1.0 + g2 - 2.0 * MIE_G * cosine, 1.5));

    // Lambert shading here has no 1/pi, so radiance is scaled by pi to match it.
    return PI * intensity * (RAYLEIGH * phaseR * rayleigh + MIE * phaseM * mie);
}
